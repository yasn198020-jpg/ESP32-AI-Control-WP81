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

            RaiseChanged(item);
            return item.Id;
        }

        public async Task UpdateAsync(string id, string command, DateTimeOffset executeAt)
        {
            await EnsureLoadedAsync();
            ScheduledCommand item = null;

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
                if (string.IsNullOrWhiteSpace(command))
                    throw new ArgumentException("Команда для расписания не может быть пустой.", "command");

                var previousCommand = item.Command;
                var previousExecuteAt = item.ExecuteAt;
                var previousStatus = item.Status;
                var previousError = item.LastError;
                item.Command = command.Trim();
                item.ExecuteAt = executeAt;
                item.Status = "Scheduled";
                item.LastError = null;

                try
                {
                    // Persist while the item is reserved by the same lock.
                    // SaveAsync takes its snapshot after acquiring the save gate.
                }
                catch
                {
                    item.Command = previousCommand;
                    item.ExecuteAt = previousExecuteAt;
                    item.Status = previousStatus;
                    item.LastError = previousError;
                    throw;
                }
            }

            await SaveAsync();
            RaiseChanged(item);
        }

        public async Task CancelAsync(string taskId)
        {
            await EnsureLoadedAsync();
            ScheduledCommand item = null;

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
                item.Status = "Cancelled";
            }

            await SaveAsync();
            RaiseChanged(item);
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
                lock (_gate)
                {
                    var now = DateTimeOffset.Now;
                    foreach (var item in _items)
                    {
                        if (item.Status == "Scheduled" && item.ExecuteAt <= now)
                        {
                            item.Status = "Executing";
                            due.Add(item.Clone());
                        }
                    }
                }

                if (due.Count == 0) return;

                try
                {
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

                foreach (var copy in due)
                {
                    var original = Find(copy.Id);
                    if (original == null) continue;

                    lock (_gate)
                    {
                        if (original.Status != "Executing")
                            continue;
                    }

                    try
                    {
                        await _executor.ExecuteAsync(copy.Command);
                        lock (_gate)
                        {
                            if (original.Status == "Executing")
                            {
                                original.Status = "Completed";
                                original.CompletedAt = DateTimeOffset.Now;
                                original.LastError = null;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (_gate)
                        {
                            if (original.Status == "Executing")
                            {
                                original.Status = "Failed";
                                original.CompletedAt = DateTimeOffset.Now;
                                original.LastError = ex.Message;
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

                    RaiseChanged(original);
                }
            }
            catch (Exception ex)
            {
                // Never let an async-void timer exception escape the ThreadPool callback.
                foreach (var copy in due)
                {
                    var item = Find(copy.Id);
                    if (item == null) continue;
                    lock (_gate)
                    {
                        if (item.Status == "Executing")
                        {
                            item.Status = "Scheduled";
                            item.LastError = "Ошибка планировщика: " + ex.Message;
                        }
                    }
                    RaiseChanged(item);
                }
                try { await SaveAsync(); } catch { }
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