using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Windows.System.Threading;
using Windows.Storage;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Scheduling
{
    public sealed class ScheduledCommandService : ISchedulerService
    {
        private const string FileName = "scheduled-commands.json";
        private readonly ICommandExecutor _executor;
        private readonly object _gate = new object();
        private readonly List<ScheduledCommand> _items = new List<ScheduledCommand>();
        private ThreadPoolTimer _timer;
        private bool _loaded;

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

            var item = new ScheduledCommand
            {
                Id = Guid.NewGuid().ToString("N"),
                Command = command,
                ExecuteAt = executeAt,
                Status = "Scheduled",
                CreatedAt = DateTimeOffset.Now
            };

            lock (_gate) _items.Add(item);
            await SaveAsync();
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

                item.Command = command;
                item.ExecuteAt = executeAt;
                item.Status = "Scheduled";
                item.LastError = null;
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
            List<ScheduledCommand> due = new List<ScheduledCommand>();

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

            await SaveAsync();

            foreach (var copy in due)
            {
                ScheduledCommand original = Find(copy.Id);
                if (original == null) continue;

                try
                {
                    await _executor.ExecuteAsync(copy.Command);
                    lock (_gate)
                    {
                        original.Status = "Completed";
                        original.CompletedAt = DateTimeOffset.Now;
                        original.LastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        original.Status = "Failed";
                        original.CompletedAt = DateTimeOffset.Now;
                        original.LastError = ex.Message;
                    }
                }

                await SaveAsync();
                RaiseChanged(original);
            }
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
                                    item.Status = "Scheduled";
                                _items.Add(item);
                            }
                        }
                    }
                }
            }

            _loaded = true;
        }

        private async Task SaveAsync()
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
                var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());

                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
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