using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Windows.Storage;

namespace ESP32AIControl.UWP.Core.Marfa
{
    /// <summary>UWP persistence adapter for the platform-neutral Android Marfa scenario model.</summary>
    public sealed class StoredDeviceScenarioStore
    {
        private const string Key = "marfa_device_scenarios_v1";
        private readonly object gate = new object();
        private List<StoredDeviceScenario> items;

        public StoredDeviceScenarioStore() { LoadLocked(); }

        public IList<StoredDeviceScenario> GetAll()
        {
            lock (gate) return items.Select(Clone).ToList();
        }

        public void Save(StoredDeviceScenario scenario)
        {
            if (scenario == null) throw new ArgumentNullException("scenario");
            if (String.IsNullOrWhiteSpace(scenario.Title)) throw new ArgumentException("Название сценария не задано.");
            lock (gate)
            {
                int index = items.FindIndex(x => x.Id == scenario.Id);
                if (index < 0) items.Add(Clone(scenario)); else items[index] = Clone(scenario);
                PersistLocked();
            }
        }

        public bool Delete(string id)
        {
            lock (gate)
            {
                int count = items.RemoveAll(x => String.Equals(x.Id, id, StringComparison.Ordinal));
                if (count > 0) PersistLocked();
                return count > 0;
            }
        }

        private void LoadLocked()
        {
            lock (gate)
            {
                try
                {
                    object raw;
                    string json = ApplicationData.Current.LocalSettings.Values.TryGetValue(Key, out raw) ? Convert.ToString(raw) : "";
                    items = String.IsNullOrWhiteSpace(json)
                        ? new List<StoredDeviceScenario>()
                        : (JsonConvert.DeserializeObject<List<StoredDeviceScenario>>(json) ?? new List<StoredDeviceScenario>());
                }
                catch (Exception ex)
                {
                    items = new List<StoredDeviceScenario>();
                    MarfaCoreDiagnostics.Log("Scenario store load failed: " + ex.Message);
                }
            }
        }

        private void PersistLocked()
        {
            try { ApplicationData.Current.LocalSettings.Values[Key] = JsonConvert.SerializeObject(items); }
            catch (Exception ex) { MarfaCoreDiagnostics.Log("Scenario store save failed: " + ex.Message); }
        }

        private static StoredDeviceScenario Clone(StoredDeviceScenario x)
        {
            return new StoredDeviceScenario
            {
                Id = x.Id, Title = x.Title, Source = x.Source,
                SensorIds = (x.SensorIds ?? new List<string>()).ToList(), Enabled = x.Enabled
            };
        }
    }

    /// <summary>
    /// Conservative scenario-aware candidate refinement. It only narrows a
    /// shortlist when a single candidate is explicitly referenced by an
    /// enabled, successfully parsed stored scenario.
    /// </summary>
    public sealed class StoredScenarioAdvisor : IMarfaScenarioAdvisor
    {
        private readonly StoredDeviceScenarioStore store;
        public StoredScenarioAdvisor(StoredDeviceScenarioStore scenarioStore) { store = scenarioStore; }

        public IList<string> RefineControlCandidatesBeforeClarification(
            IList<string> candidateIds, string desiredValue, IList<MarfaDevice> devices)
        {
            if (candidateIds == null || candidateIds.Count < 2 || store == null) return new List<string>();
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scenario in store.GetAll().Where(x => x.Enabled))
            {
                var parsed = IoTScenarioParser.Parse(scenario.Source);
                if (parsed.Errors.Count > 0) continue;
                var model = IoTScenarioSemanticAnalyzer.Analyze(parsed);
                foreach (var id in scenario.SensorIds ?? new List<string>()) referenced.Add(id);
                foreach (var id in model.Identifiers) referenced.Add(id);
            }
            var matches = candidateIds.Where(id => referenced.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return matches.Count == 1 ? matches : new List<string>();
        }
    }
}