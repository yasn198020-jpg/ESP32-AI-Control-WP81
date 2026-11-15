using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ESP32AIControl.WP81.Core.State;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    internal sealed class ScenarioDeviation
    {
        public string Key { get; set; }
        public string ScenarioId { get; set; }
        public string ScenarioTitle { get; set; }
        public string DeviceId { get; set; }
        public int RuleIndex { get; set; }
        public string WidgetId { get; set; }
        public string Expected { get; set; }
        public string Actual { get; set; }
        public string Condition { get; set; }
        public DateTimeOffset Since { get; set; }

        public string Message
        {
            get
            {
                return "«" + (ScenarioTitle ?? "Сценарий") + "»: при условии " +
                       (Condition ?? string.Empty) + " элемент " +
                       (WidgetId ?? string.Empty) + " должен иметь значение " +
                       (Expected ?? string.Empty) + ", сейчас " +
                       (Actual ?? "неизвестно") + ".";
            }
        }
    }

    internal sealed class ScenarioDeviationChangedEventArgs : EventArgs
    {
        public IList<ScenarioDeviation> Deviations { get; private set; }
        public ScenarioDeviationChangedEventArgs(IList<ScenarioDeviation> deviations)
        {
            Deviations = deviations ?? new List<ScenarioDeviation>();
        }
    }

    internal sealed class ScenarioDeviationMonitor
    {
        private sealed class CompiledProfile
        {
            public ScenarioProfile Profile;
            public ScenarioLanguageModel Model;
        }

        private sealed class PendingDeviation
        {
            public ScenarioDeviation Deviation;
            public long Generation;
        }

        private const int GraceMilliseconds = 4000;
        private readonly DeviceRepository _devices;
        private readonly object _sync = new object();
        private readonly Dictionary<string, PendingDeviation> _pending =
            new Dictionary<string, PendingDeviation>(StringComparer.Ordinal);
        private readonly Dictionary<string, ScenarioDeviation> _active =
            new Dictionary<string, ScenarioDeviation>(StringComparer.Ordinal);
        private readonly List<CompiledProfile> _compiled = new List<CompiledProfile>();
        private readonly List<string> _diagnostics = new List<string>();
        private long _generation;

        public event EventHandler<ScenarioDeviationChangedEventArgs> DeviationsChanged;

        public ScenarioDeviationMonitor(DeviceRepository devices)
        {
            if (devices == null) throw new ArgumentNullException("devices");
            _devices = devices;
        }

        public IList<string> Diagnostics
        {
            get
            {
                lock (_sync) return _diagnostics.ToList();
            }
        }

        public void SetProfiles(IEnumerable<ScenarioProfile> profiles)
        {
            var compiled = new List<CompiledProfile>();
            var diagnostics = new List<string>();

            if (profiles != null)
            {
                foreach (var profile in profiles)
                {
                    if (profile == null || !profile.Enabled || string.IsNullOrWhiteSpace(profile.Source))
                        continue;

                    var model = ScenarioLanguageParser.Compile(profile.Source);
                    if (model.Errors.Count > 0)
                    {
                        diagnostics.Add((profile.Title ?? "Сценарий") + ": " +
                            string.Join("; ", model.Errors.ToArray()));
                        continue;
                    }

                    if (model.Rules.Count == 0)
                    {
                        diagnostics.Add((profile.Title ?? "Сценарий") +
                            ": правила IF/THEN не найдены.");
                        continue;
                    }

                    compiled.Add(new CompiledProfile
                    {
                        Profile = profile.Clone(),
                        Model = model
                    });
                }
            }

            IList<ScenarioDeviation> snapshot;
            lock (_sync)
            {
                _compiled.Clear();
                _compiled.AddRange(compiled);
                _diagnostics.Clear();
                _diagnostics.AddRange(diagnostics);
                _pending.Clear();
                _active.Clear();
                _generation++;
                snapshot = SnapshotLocked();
            }

            RaiseChanged(snapshot);
        }

        public void Observe()
        {
            var mismatches = EvaluateCurrent();
            IList<ScenarioDeviation> snapshot = null;
            var changed = false;

            lock (_sync)
            {
                var currentKeys = new HashSet<string>(mismatches.Keys, StringComparer.Ordinal);

                foreach (var pair in mismatches)
                {
                    ScenarioDeviation existing;
                    if (_active.TryGetValue(pair.Key, out existing))
                    {
                        if (!SamePresentation(existing, pair.Value))
                        {
                            pair.Value.Since = existing.Since;
                            _active[pair.Key] = pair.Value;
                            changed = true;
                        }
                        continue;
                    }

                    PendingDeviation pending;
                    if (_pending.TryGetValue(pair.Key, out pending))
                    {
                        pending.Deviation.Actual = pair.Value.Actual;
                        pending.Deviation.Since = pending.Deviation.Since;
                    }
                    else
                    {
                        var generation = ++_generation;
                        var first = pair.Value;
                        first.Since = DateTimeOffset.Now;
                        _pending[pair.Key] = new PendingDeviation
                        {
                            Deviation = first,
                            Generation = generation
                        };
                        ScheduleConfirmation(pair.Key, generation);
                    }
                }

                var pendingToRemove = _pending.Keys.Where(key => !currentKeys.Contains(key)).ToList();
                foreach (var key in pendingToRemove) _pending.Remove(key);

                var activeToRemove = _active.Keys.Where(key => !currentKeys.Contains(key)).ToList();
                if (activeToRemove.Count > 0)
                {
                    foreach (var key in activeToRemove) _active.Remove(key);
                    changed = true;
                }

                if (changed) snapshot = SnapshotLocked();
            }

            if (changed) RaiseChanged(snapshot);
        }

        private Dictionary<string, ScenarioDeviation> EvaluateCurrent()
        {
            var result = new Dictionary<string, ScenarioDeviation>(StringComparer.Ordinal);
            var widgets = _devices.GetWidgetsSnapshot();
            var widgetsById = new Dictionary<string, WidgetState>(StringComparer.OrdinalIgnoreCase);
            var duplicateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var widget in widgets)
            {
                if (widget == null || string.IsNullOrWhiteSpace(widget.Id)) continue;
                if (widgetsById.ContainsKey(widget.Id))
                {
                    duplicateIds.Add(widget.Id);
                    continue;
                }
                widgetsById[widget.Id] = widget;
            }

            foreach (var duplicate in duplicateIds)
                widgetsById.Remove(duplicate);

            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in widgetsById)
                variables[pair.Key] = pair.Value.Value ?? string.Empty;

            List<CompiledProfile> profiles;
            lock (_sync) profiles = _compiled.ToList();

            foreach (var compiled in profiles)
            {
                foreach (var rule in compiled.Model.Rules)
                {
                    var condition = ScenarioLanguageEvaluator.Evaluate(rule.Condition, variables);
                    if (!ScenarioLanguageEvaluator.IsTruthy(condition)) continue;

                    foreach (var action in rule.Actions)
                    {
                        WidgetState target;
                        if (!widgetsById.TryGetValue(action.TargetId, out target)) continue;

                        var expected = ScenarioLanguageEvaluator.AsComparableText(
                            ScenarioLanguageEvaluator.Evaluate(action.Expression, variables));
                        if (expected == null) continue;

                        var actual = (target.Value ?? string.Empty).Trim();
                        if (ScenarioLanguageEvaluator.ValuesEquivalent(actual, expected)) continue;

                        var key = compiled.Profile.Id + "/" +
                                  rule.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                                  action.TargetId + "/" + expected;
                        result[key] = new ScenarioDeviation
                        {
                            Key = key,
                            ScenarioId = compiled.Profile.Id,
                            ScenarioTitle = compiled.Profile.Title,
                            DeviceId = target.DeviceId,
                            RuleIndex = rule.Index,
                            WidgetId = action.TargetId,
                            Expected = expected,
                            Actual = string.IsNullOrWhiteSpace(actual) ? "неизвестно" : actual,
                            Condition = rule.RenderedCondition,
                            Since = DateTimeOffset.Now
                        };
                    }
                }
            }

            return result;
        }

        private void ScheduleConfirmation(string key, long generation)
        {
            Task.Delay(GraceMilliseconds).ContinueWith(delegate
            {
                ConfirmAfterGrace(key, generation);
            });
        }

        private void ConfirmAfterGrace(string key, long generation)
        {
            var latest = EvaluateCurrent();
            ScenarioDeviation current;
            latest.TryGetValue(key, out current);

            IList<ScenarioDeviation> snapshot = null;
            lock (_sync)
            {
                PendingDeviation pending;
                if (!_pending.TryGetValue(key, out pending) || pending.Generation != generation)
                    return;

                if (current == null)
                {
                    _pending.Remove(key);
                    return;
                }

                current.Since = pending.Deviation.Since;
                _pending.Remove(key);
                _active[key] = current;
                snapshot = SnapshotLocked();
            }

            RaiseChanged(snapshot);
        }

        private static bool SamePresentation(ScenarioDeviation a, ScenarioDeviation b)
        {
            return string.Equals(a.Actual, b.Actual, StringComparison.Ordinal) &&
                   string.Equals(a.Expected, b.Expected, StringComparison.Ordinal) &&
                   string.Equals(a.Condition, b.Condition, StringComparison.Ordinal);
        }

        private IList<ScenarioDeviation> SnapshotLocked()
        {
            return _active.Values
                .OrderByDescending(item => item.Since)
                .Select(Clone)
                .ToList();
        }

        private static ScenarioDeviation Clone(ScenarioDeviation source)
        {
            return new ScenarioDeviation
            {
                Key = source.Key,
                ScenarioId = source.ScenarioId,
                ScenarioTitle = source.ScenarioTitle,
                DeviceId = source.DeviceId,
                RuleIndex = source.RuleIndex,
                WidgetId = source.WidgetId,
                Expected = source.Expected,
                Actual = source.Actual,
                Condition = source.Condition,
                Since = source.Since
            };
        }

        private void RaiseChanged(IList<ScenarioDeviation> snapshot)
        {
            var handler = DeviationsChanged;
            if (handler != null)
                handler(this, new ScenarioDeviationChangedEventArgs(snapshot));
        }
    }
}