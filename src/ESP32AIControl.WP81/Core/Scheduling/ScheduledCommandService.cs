using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.System.Threading;
using Windows.Storage;
using ESP32AIControl.WP81.Models;
using ESP32AIControl.WP81.Core.Scenarios;

namespace ESP32AIControl.WP81.Core.Scheduling
{
    public sealed class ScheduledCommandService : ISchedulerService
    {
        private const string FileName = "scheduled-commands.json";
        private readonly ICommandExecutor _executor;
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _saveGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _loadGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _mutationGate = new SemaphoreSlim(1, 1);
        private readonly HashSet<string> _mutating = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<ScheduledCommand> _items = new List<ScheduledCommand>();
        private ThreadPoolTimer _timer;
        private bool _loaded;
        private int _tickRunning;

        public event EventHandler<ScheduledCommand> TaskChanged;

        public ScheduledCommandService(ICommandExecutor executor)
        {
            _executor = executor;
        }

        public async Task InitializeAsync()
        {
            await LoadAsync();
            if (_timer == null)
                _timer = ThreadPoolTimer.CreatePeriodicTimer(Tick, TimeSpan.FromSeconds(5));
        }

        public async Task<string> ScheduleAsync(string command, DateTimeOffset executeAt)
        {
            await EnsureLoadedAsync();
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Команда для расписания не может быть пустой.", "command");

            var item = new ScheduledCommand
            {
                Id = Guid.NewGuid().ToString("N"),
                Command = command.Trim(),
                ExecuteAt = executeAt,
                Status = "Scheduled",
                CreatedAt = DateTimeOffset.Now
            };

            await _mutationGate.WaitAsync();
            try
            {
                lock (_gate) _items.Add(item);
                try
                {
                    await SaveAsync();
                }
                catch
                {
                    lock (_gate) _items.RemoveAll(x => x.Id == item.Id);
                    throw;
                }
            }
            finally
            {
                _mutationGate.Release();
            }

            RaiseChanged(item);
            return item.Id;
        }

        public async Task UpdateAsync(string id, string command, DateTimeOffset executeAt)
        {
            await EnsureLoadedAsync();
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Команда для расписания не может быть пустой.", "command");

            await _mutationGate.WaitAsync();
            try
            {
                ScheduledCommand item = null;
                string previousCommand = null;
                DateTimeOffset previousExecuteAt = DateTimeOffset.MinValue;
                string previousStatus = null;
                string previousError = null;

                lock (_gate)
                {
                    for (var i = 0; i < _items.Count; i++)
                    {
                        if (_items[i].Id == id)
                        {
                            item = _items[i];
                            break;
                        }
                    }

                    if (item == null) throw new InvalidOperationException("Задача не найдена.");
                    if (item.Status == "Completed" || item.Status == "Cancelled")
                        throw new InvalidOperationException("Завершённую задачу нельзя изменить.");
                    if (item.Status == "Executing")
                        throw new InvalidOperationException("Команда уже выполняется. Изменить её сейчас нельзя.");
                    if (!_mutating.Add(id))
                        throw new InvalidOperationException("Эта задача уже изменяется.");

                    previousCommand = item.Command;
                    previousExecuteAt = item.ExecuteAt;
                    previousStatus = item.Status;
                    previousError = item.LastError;
                    item.Command = command.Trim();
                    item.ExecuteAt = executeAt;
                    item.Status = "Scheduled";
                    item.LastError = null;
                }

                try
                {
                    await SaveAsync();
                }
                catch
                {
                    lock (_gate)
                    {
                        item.Command = previousCommand;
                        item.ExecuteAt = previousExecuteAt;
                        item.Status = previousStatus;
                        item.LastError = previousError;
                    }
                    throw;
                }
                finally
                {
                    lock (_gate) _mutating.Remove(id);
                }

                RaiseChanged(item);
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        public async Task CancelAsync(string taskId)
        {
            await EnsureLoadedAsync();
            await _mutationGate.WaitAsync();
            try
            {
                ScheduledCommand item = null;
                string previousStatus = null;

                lock (_gate)
                {
                    for (var i = 0; i < _items.Count; i++)
                    {
                        if (_items[i].Id == taskId)
                        {
                            item = _items[i];
                            break;
                        }
                    }

                    if (item == null) return;
                    if (item.Status == "Completed" || item.Status == "Cancelled") return;
                    if (item.Status == "Executing")
                        throw new InvalidOperationException("Команда уже выполняется и не может быть отменена.");
                    if (!_mutating.Add(taskId))
                        throw new InvalidOperationException("Эта задача уже изменяется.");

                    previousStatus = item.Status;
                    item.Status = "Cancelled";
                }

                try
                {
                    await SaveAsync();
                }
                catch
                {
                    lock (_gate) item.Status = previousStatus;
                    throw;
                }
                finally
                {
                    lock (_gate) _mutating.Remove(taskId);
                }

                RaiseChanged(item);
            }
            finally
            {
                _mutationGate.Release();
            }
        }

        public async Task<IList<ScheduledCommand>> GetAllAsync()
        {
            await EnsureLoadedAsync();
            var result = new List<ScheduledCommand>();
            lock (_gate)
            {
                foreach (var item in _items)
                    result.Add(item.Clone());
            }
            result.Sort(delegate(ScheduledCommand a, ScheduledCommand b)
            {
                return a.ExecuteAt.CompareTo(b.ExecuteAt);
            });
            return result;
        }

        private async void Tick(ThreadPoolTimer timer)
        {
            if (Interlocked.Exchange(ref _tickRunning, 1) != 0)
                return;

            var due = new List<ScheduledCommand>();
            try
            {
                await _mutationGate.WaitAsync();
                try
                {
                    lock (_gate)
                    {
                        var now = DateTimeOffset.Now;
                        foreach (var item in _items)
                        {
                            if (item.Status == "Scheduled" &&
                                !_mutating.Contains(item.Id) &&
                                item.ExecuteAt <= now)
                            {
                                item.Status = "Executing";
                                due.Add(item.Clone());
                            }
                        }
                    }

                    if (due.Count > 0)
                    {
                        try
                        {
                            // Persist the execution claim before sending anything.
                            // A restart can then mark interrupted work as unknown
                            // instead of replaying a command that may have reached the device.
                            await SaveAsync();
                        }
                        catch (Exception persistenceError)
                        {
                            lock (_gate)
                            {
                                foreach (var copy in due)
                                {
                                    var item = FindUnsafe(copy.Id);
                                    if (item != null && item.Status == "Executing")
                                    {
                                        item.Status = "Scheduled";
                                        item.LastError = "Не удалось сохранить состояние очереди: " + persistenceError.Message;
                                    }
                                }
                            }
                            foreach (var copy in due)
                            {
                                var item = Find(copy.Id);
                                if (item != null) RaiseChanged(item);
                            }
                            return;
                        }
                    }
                }
                finally
                {
                    _mutationGate.Release();
                }

                foreach (var copy in due)
                {
                    var original = Find(copy.Id);
                    if (original == null) continue;

                    lock (_gate)
                    {
                        if (original.Status != "Executing")
                            continue;
                    }

                    var completed = false;
                    string executionError = null;
                    try
                    {
                        await _executor.ExecuteAsync(copy.Command);
                        completed = true;
                    }
                    catch (Exception ex)
                    {
                        executionError = ex.Message;
                    }

                    await _mutationGate.WaitAsync();
                    try
                    {
                        lock (_gate)
                        {
                            if (original.Status == "Executing")
                            {
                                if (completed)
                                {
                                    original.Status = "Completed";
                                    original.CompletedAt = DateTimeOffset.Now;
                                    original.LastError = null;
                                }
                                else
                                {
                                    original.Status = "Failed";
                                    original.CompletedAt = DateTimeOffset.Now;
                                    original.LastError = executionError;
                                }
                            }
                        }

                        try
                        {
                            await SaveAsync();
                        }
                        catch (Exception persistenceError)
                        {
                            lock (_gate)
                            {
                                if (original.Status == "Completed")
                                    original.LastError = "Команда выполнена, но результат не удалось сохранить: " + persistenceError.Message;
                                else if (original.Status == "Failed")
                                    original.LastError = (original.LastError ?? string.Empty) +
                                        " Не удалось сохранить итоговый статус: " + persistenceError.Message;
                            }
                        }
                    }
                    finally
                    {
                        _mutationGate.Release();
                    }

                    RaiseChanged(original);
                }
            }
            catch (Exception ex)
            {
                // Never let an async-void timer exception escape the ThreadPool callback.
                await _mutationGate.WaitAsync();
                try
                {
                    foreach (var copy in due)
                    {
                        var item = Find(copy.Id);
                        if (item == null) continue;
                        lock (_gate)
                        {
                            // Only work that has not crossed the execution boundary
                            // may be returned to Scheduled.
                            if (item.Status == "Executing")
                            {
                                item.Status = "Scheduled";
                                item.LastError = "Ошибка планировщика: " + ex.Message;
                            }
                        }
                    }
                    try { await SaveAsync(); } catch { }
                }
                finally
                {
                    _mutationGate.Release();
                }

                foreach (var copy in due)
                {
                    var item = Find(copy.Id);
                    if (item != null) RaiseChanged(item);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        private ScheduledCommand FindUnsafe(string id)
        {
            for (var i = 0; i < _items.Count; i++)
                if (_items[i].Id == id) return _items[i];
            return null;
        }

        private ScheduledCommand Find(string id)
        {
            lock (_gate)
            {
                for (var i = 0; i < _items.Count; i++)
                    if (_items[i].Id == id) return _items[i];
            }
            return null;
        }

        private async Task EnsureLoadedAsync()
        {
            if (!_loaded) await LoadAsync();
        }

        private async Task LoadAsync()
        {
            await _loadGate.WaitAsync();
            try
            {
                if (_loaded) return;

                StorageFile file;
            try
            {
                file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
            }
            catch (FileNotFoundException)
            {
                _loaded = true;
                return;
            }

            var json = await FileIO.ReadTextAsync(file);
            var recoveredInterruptedExecution = false;
            if (!string.IsNullOrWhiteSpace(json))
            {
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<ScheduledCommand>));
                    var list = serializer.ReadObject(stream) as List<ScheduledCommand>;
                    if (list != null)
                    {
                        lock (_gate)
                        {
                            _items.Clear();
                            foreach (var item in list)
                            {
                                if (item.Status == "Executing")
                                {
                                    // The command may have reached the device before the app was closed.
                                    // Never replay it automatically: its physical result is unknown.
                                    item.Status = "Failed";
                                    item.CompletedAt = DateTimeOffset.Now;
                                    item.LastError = "Приложение было закрыто во время выполнения. Результат неизвестен; проверьте устройство перед повтором.";
                                    recoveredInterruptedExecution = true;
                                }
                                _items.Add(item);
                            }
                        }
                    }
                }
            }

            _loaded = true;
                if (recoveredInterruptedExecution)
                    await SaveAsync();
            }
            finally
            {
                _loadGate.Release();
            }
        }

        private async Task SaveAsync()
        {
            await _saveGate.WaitAsync();
            try
            {
                List<ScheduledCommand> snapshot = new List<ScheduledCommand>();
                lock (_gate)
                {
                    foreach (var item in _items)
                        snapshot.Add(item.Clone());
                }

                using (var stream = new MemoryStream())
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<ScheduledCommand>));
                    serializer.WriteObject(stream, snapshot);
                    var json = System.Text.Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);

                    var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                        FileName, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteTextAsync(file, json);
                }
            }
            finally
            {
                _saveGate.Release();
            }
        }

        private void RaiseChanged(ScheduledCommand item)
        {
            var handler = TaskChanged;
            if (handler != null)
                handler(this, item.Clone());
        }
    }
}