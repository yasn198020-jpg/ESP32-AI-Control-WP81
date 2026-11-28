using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ESP32AIControl.UWP.Core.Marfa
{
    /// <summary>
    /// Deterministic target resolver ported from Android MarfaAnalyticalEngine.
    /// This class only selects a widget; it never sends MQTT commands.
    /// </summary>
    public sealed class MarfaAnalyticalEngine
    {
        public enum Kind { TEMPERATURE, HUMIDITY, PRESSURE, VALUE }
        public enum EntityKind { DOOR, VENT, WINDOW, GATE, PUMP, FAN, HEATER, VALVE, LIGHT, IRRIGATION, GENERIC }

        public sealed class ControlCandidate
        {
            public MarfaDevice Device { get; set; }
            public MarfaWidgetState Widget { get; set; }
            public int Score { get; set; }
            public IList<string> Reasons { get; set; }
        }
        public sealed class SensorCandidate
        {
            public MarfaDevice Device { get; set; }
            public MarfaWidgetState Widget { get; set; }
            public int Score { get; set; }
            public IList<string> Reasons { get; set; }
        }
        public sealed class ControlResolution
        {
            public ControlCandidate Candidate { get; set; }
            public IList<ControlCandidate> Candidates { get; set; }
            public string Clarification { get; set; }
            public ControlResolution() { Candidates = new List<ControlCandidate>(); }
        }
        public sealed class SensorResolution
        {
            public SensorCandidate Candidate { get; set; }
            public IList<SensorCandidate> Candidates { get; set; }
            public string Clarification { get; set; }
            public SensorResolution() { Candidates = new List<SensorCandidate>(); }
        }

        private static readonly string[] PhysicalWords = { "реле", "выход", "выходной", "кнопк", "gpio", "канал", "исполнитель" };
        private static readonly string[] StateWords = { "состояние", "статус", "индикатор", "положение" };

        public ControlResolution ResolveControl(string text, IList<MarfaDevice> devices, string desiredValue = null)
        {
            devices = devices ?? new List<MarfaDevice>();
            string normalized = Normalize(text);
            string page = ExplicitPage(normalized, devices);
            string exactId = ExplicitElementId(normalized, devices);
            var all = devices.SelectMany(d => SafeWidgets(d).Where(IsControllable).Select(w =>
                new ControlCandidate { Device = d, Widget = w, Score = ControlScore(normalized, page, exactId, d, w, desiredValue), Reasons = ControlReasons(normalized, page, exactId, w, desiredValue) })).ToList();

            if (!String.IsNullOrEmpty(exactId))
            {
                var matches = all.Where(c => String.Equals(c.Widget.Id, exactId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1) return new ControlResolution { Candidate = matches[0], Candidates = matches };
                return new ControlResolution { Candidates = matches, Clarification = matches.Count > 1
                    ? "ID элемента «" + exactId + "» найден несколько раз. Нужен уникальный ID."
                    : "Элемент с ID «" + exactId + "» не найден." };
            }

            var contextPages = page == null ? MatchingContextPages(normalized, devices, desiredValue) : new List<string>();
            var unknown = page == null && contextPages.Count == 0 ? UnknownContextWords(normalized, devices) : new List<string>();
            if (unknown.Count > 0)
                return new ControlResolution { Clarification = "Я нашла объект «" + EntityName(DetectEntityKind(normalized)) +
                    "», но не нашла совпадение контекста «" + String.Join(", ", unknown) +
                    "». Уточните вкладку. Доступные вкладки: " + AvailablePageNames(devices) + "." };

            IEnumerable<ControlCandidate> scoped = all;
            if (page != null) scoped = scoped.Where(c => Normalize(c.Widget.Page) == page);
            else if (contextPages.Count > 0) scoped = scoped.Where(c => contextPages.Contains(Normalize(c.Widget.Page)));

            var entity = DetectEntityKind(normalized);
            if (entity != EntityKind.GENERIC) scoped = scoped.Where(c => EntityMatches(entity, c.Widget));

            bool explicitPhysical = ContainsAny(normalized, PhysicalWords);
            if (!explicitPhysical && entity != EntityKind.GENERIC)
            {
                var logical = scoped.Where(c => IsLogicalStateWidget(c.Widget)).ToList();
                if (logical.Count > 0) scoped = logical;
            }

            var candidates = scoped.Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score).ThenBy(c => c.Widget.Order)
                .ThenBy(c => Normalize(c.Widget.Page)).ThenBy(c => c.Widget.Id ?? "").ToList();
            if (candidates.Count == 0)
            {
                string suffix = page == null ? "" : " на вкладке «" + PrettyPage(page, devices) + "»";
                return new ControlResolution { Clarification = "Я не нашла однозначный управляемый элемент" + suffix +
                    ". Назовите объект, ID элемента или вкладку." };
            }

            bool contextResolved = page != null || contextPages.Count == 1;
            if (candidates.Count == 1 && contextResolved)
                return new ControlResolution { Candidate = candidates[0], Candidates = candidates };

            int best = candidates[0].Score;
            bool tied = candidates.Count(c => c.Score == best) > 1;
            bool weak = best < 22;
            bool tooClose = candidates.Count > 1 && candidates[1].Score >= 18 && best - candidates[1].Score < 5;
            if (tied || weak || tooClose)
                return new ControlResolution { Candidates = candidates.Take(5).ToList(), Clarification = ClarificationForControls(candidates) };
            return new ControlResolution { Candidate = candidates[0], Candidates = candidates };
        }

        public SensorResolution ResolveSensor(string text, IList<MarfaDevice> devices)
        {
            devices = devices ?? new List<MarfaDevice>();
            string normalized = Normalize(text);
            string page = ExplicitPage(normalized, devices);
            Kind kind = DetectKind(normalized);
            bool limitSwitch = ContainsAny(normalized, "концевик", "концевого выключателя", "концевой выключатель", "концевой датчик", "limit switch");
            bool asksAutomation = ContainsAny(normalized, "положен", "состоян", "статус", "включен", "выключен", "работает", "активен") &&
                ContainsAny(normalized, "автомат", "управлен");
            var all = new List<SensorCandidate>();
            foreach (var device in devices)
            foreach (var widget in SafeWidgets(device))
            {
                bool supported = widget.Type == MarfaWidgetType.VALUE || widget.Type == MarfaWidgetType.STATUS ||
                    (asksAutomation && widget.Type == MarfaWidgetType.TOGGLE && ContainsAny(Normalize(widget.Title), "автомат", "управлен"));
                if (!supported) continue;
                if (limitSwitch)
                {
                    string corpus = Normalize((widget.Title ?? "") + " " + (widget.DefinitionName ?? "") + " " + (widget.ConfigJson ?? ""));
                    if ((widget.Type != MarfaWidgetType.VALUE && widget.Type != MarfaWidgetType.STATUS) ||
                        !ContainsAny(corpus, "концевик", "концевого выключателя", "концевой выключатель", "концевой датчик", "limit switch")) continue;
                }
                int score = SensorScore(normalized, kind, page, device, widget);
                if (score > 0) all.Add(new SensorCandidate { Device = device, Widget = widget, Score = score, Reasons = SensorReasons(normalized, kind, page, widget) });
            }
            if (limitSwitch && all.Count == 0)
                return new SensorResolution { Clarification = "Не нашла концевик в данных устройств. Проверьте, что виджет концевика передаётся Марфе как датчик или статус." };

            var contextPages = page == null ? MatchingContextPages(normalized, devices, null) : new List<string>();
            var contextTokens = ContextTokens(normalized, devices);
            IEnumerable<SensorCandidate> scoped = all;
            if (page != null) scoped = scoped.Where(c => Normalize(c.Widget.Page) == page);
            else if (contextTokens.Count > 0)
            {
                var matching = all.Where(c => contextTokens.All(t => ContainsSemanticToken(
                    Normalize((c.Device.Name ?? "") + " " + (c.Widget.Page ?? "") + " " + (c.Widget.Title ?? "") + " " + (c.Widget.DefinitionName ?? "")), t))).ToList();
                if (matching.Count == 0)
                    return new SensorResolution { Clarification = "Я нашла датчик, но не нашла совпадение контекста «" +
                        String.Join(", ", contextTokens) + "». Уточните вкладку или объект. Доступные вкладки: " + AvailablePageNames(devices) + "." };
                scoped = matching;
            }
            else if (contextPages.Count > 0) scoped = scoped.Where(c => contextPages.Contains(Normalize(c.Widget.Page)));
            else
            {
                var unknown = UnknownContextWords(normalized, devices);
                if (unknown.Count > 0) return new SensorResolution { Clarification = "Я нашла датчик, но не нашла совпадение контекста «" +
                    String.Join(", ", unknown) + "». Уточните вкладку или объект. Доступные вкладки: " + AvailablePageNames(devices) + "." };
            }

            var candidates = scoped.OrderByDescending(c => c.Score).ThenBy(c => Normalize(c.Widget.Page))
                .ThenBy(c => c.Widget.Order).ThenBy(c => c.Widget.Id ?? "").ToList();
            if (candidates.Count == 0)
            {
                string question = kind == Kind.TEMPERATURE ? "Какую температуру показать? Укажите объект или вкладку." :
                    kind == Kind.HUMIDITY ? "Какую влажность показать? Укажите объект или вкладку." :
                    kind == Kind.PRESSURE ? "Какое давление показать? Укажите объект или вкладку." :
                    "Какой датчик показать? Назовите его или укажите вкладку.";
                return new SensorResolution { Clarification = question };
            }
            bool contextResolved = page != null || contextPages.Count == 1;
            if (candidates.Count == 1 && contextResolved)
                return new SensorResolution { Candidate = candidates[0], Candidates = candidates };
            int best = candidates[0].Score;
            bool tied = candidates.Count(c => c.Score == best) > 1;
            bool weak = best < 16;
            bool close = candidates.Count > 1 && candidates[1].Score >= 14 && best - candidates[1].Score < 4;
            if (tied || weak || close)
                return new SensorResolution { Candidates = candidates.Take(5).ToList(), Clarification = ClarificationForSensors(candidates) };
            return new SensorResolution { Candidate = candidates[0], Candidates = candidates };
        }

        public Kind DetectKind(string text)
        {
            string value = Normalize(text);
            if (ContainsAny(value, "температур", "темп", "градус", "жарко", "холодно")) return Kind.TEMPERATURE;
            if (ContainsAny(value, "влажност", "влажн")) return Kind.HUMIDITY;
            if (ContainsAny(value, "давлен")) return Kind.PRESSURE;
            return Kind.VALUE;
        }

        public string ExplicitPage(string text, IList<MarfaDevice> devices)
        {
            var marker = Regex.Match(text ?? "", @"(?:на\s+страниц(?:е|у)|на\s+вкладк(?:е|у)|во\s+вкладк(?:е|у)|в\s+вкладк(?:е|у)|страниц(?:а|у)|вкладк(?:а|у))\s+");
            if (!marker.Success) return null;
            string tail = " " + text.Substring(marker.Index + marker.Length).Trim() + " ";
            return devices.SelectMany(SafeWidgets).Select(w => Normalize(w.Page)).Where(p => p.Length > 0)
                .Distinct().OrderByDescending(p => p.Length).FirstOrDefault(p => tail.Contains(" " + p + " "));
        }

        private int ControlScore(string text, string page, string exactId, MarfaDevice device, MarfaWidgetState widget, string desired)
        {
            if (exactId != null && !String.Equals(widget.Id, exactId, StringComparison.OrdinalIgnoreCase)) return -100000;
            string title = Normalize(widget.Title), widgetPage = Normalize(widget.Page), deviceName = Normalize(device.Name);
            int score = 0;
            var aliases = EntityAliases(DetectEntityKind(text));
            if (aliases.Item1.Any(text.Contains))
            {
                if (aliases.Item2.Any(title.Contains)) score += 34;
                if (aliases.Item2.Any(widgetPage.Contains)) score += 10;
                if (aliases.Item2.Any(deviceName.Contains)) score += 8;
            }
            foreach (var token in Tokens(text).Where(t => t.Length >= 3))
            {
                string key = RussianSemanticNormalizer.Lemma(token);
                if (ContainsSemanticToken(title, key)) score += 6;
                if (ContainsSemanticToken(widgetPage, key)) score += 3;
                if (ContainsSemanticToken(deviceName, key)) score += 2;
            }
            bool physical = ContainsAny(text, PhysicalWords);
            bool hasOpen = ContainsAny(title, "открыть", "открой", "открыва", "распах", "поднять", "подъем");
            bool hasClose = ContainsAny(title, "закрыть", "закрой", "закрыва", "опустить", "опуск");
            bool state = (title.Contains("открыт") && title.Contains("закрыт")) || ContainsAny(title, StateWords);
            if (desired == "1") score += (hasOpen ? 40 : 0) + (hasClose && !hasOpen ? -18 : 0) + (state && !physical ? 60 : 0) + (physical && !state ? 45 : 0);
            else if (desired == "0") score += (hasClose ? 40 : 0) + (hasOpen && !hasClose ? -18 : 0) + (state && !physical ? 60 : 0) + (physical && !state ? 45 : 0);
            else if (state && !physical) score += 12;
            if (page != null && widgetPage != page) return -100000;
            if (page != null) score += 500;
            return score;
        }

        private int SensorScore(string text, Kind kind, string page, MarfaDevice device, MarfaWidgetState widget)
        {
            string title = Normalize(widget.Title), widgetPage = Normalize(widget.Page), deviceName = Normalize(device.Name);
            int score = 0;
            if (kind == Kind.TEMPERATURE) { if (title.Contains("температур") || title.Contains("темп")) score += 22; if ((widget.Unit ?? "").Contains("°") || String.Equals(widget.Unit, "c", StringComparison.OrdinalIgnoreCase)) score += 10; }
            if (kind == Kind.HUMIDITY) { if (title.Contains("влажн")) score += 22; if ((widget.Unit ?? "").Contains("%")) score += 7; }
            if (kind == Kind.PRESSURE) { if (title.Contains("давлен")) score += 22; if (ContainsAny((widget.Unit ?? "").ToLowerInvariant(), "па", "bar")) score += 7; }
            bool asksState = ContainsAny(text, "положен", "состоян", "статус", "открыт", "закрыт", "включен", "выключен", "режим");
            if (asksState && widget.Type == MarfaWidgetType.STATUS) score += 20;
            if (asksState && ContainsAny(title, "положен", "состоян", "статус", "открыт", "закрыт", "включен", "выключен", "режим")) score += 18;
            foreach (var token in Tokens(text).Where(t => t.Length >= 3))
            {
                string key = RussianSemanticNormalizer.Lemma(token);
                if (ContainsSemanticToken(title, key)) score += 8;
                if (ContainsSemanticToken(widgetPage, key)) score += 5;
                if (ContainsSemanticToken(deviceName, key)) score += 3;
            }
            if (Tokens(text).Any(t => String.Equals(t, widget.Id, StringComparison.OrdinalIgnoreCase))) score += 1000;
            if (page != null && widgetPage != page) return -100000;
            if (page != null) score += 500;
            return score;
        }

        private IList<string> ControlReasons(string text, string page, string id, MarfaWidgetState widget, string desired)
        {
            var result = new List<string>(); var entity = DetectEntityKind(text);
            if (entity != EntityKind.GENERIC) result.Add(entity.ToString().ToLowerInvariant());
            if (page != null && Normalize(widget.Page) == page) result.Add("вкладка " + widget.Page);
            if (id != null && String.Equals(widget.Id, id, StringComparison.OrdinalIgnoreCase)) result.Add("ID " + widget.Id);
            if (desired != null) result.Add("значение " + desired);
            if (!String.IsNullOrWhiteSpace(widget.Title)) result.Add("название " + widget.Title);
            return result;
        }
        private IList<string> SensorReasons(string text, Kind kind, string page, MarfaWidgetState widget)
        {
            var result = new List<string>(); if (kind != Kind.VALUE) result.Add(kind.ToString().ToLowerInvariant());
            if (page != null && Normalize(widget.Page) == page) result.Add("вкладка " + widget.Page);
            if (Tokens(text).Any(t => String.Equals(t, widget.Id, StringComparison.OrdinalIgnoreCase))) result.Add("ID " + widget.Id);
            if (!String.IsNullOrWhiteSpace(widget.Unit)) result.Add("единица " + widget.Unit);
            return result;
        }

        private static bool IsControllable(MarfaWidgetState w) { return w.Type == MarfaWidgetType.TOGGLE || w.Type == MarfaWidgetType.BUTTON || w.Type == MarfaWidgetType.INPUT; }
        private static bool IsLogicalStateWidget(MarfaWidgetState w)
        {
            if (w.Type != MarfaWidgetType.TOGGLE && w.Type != MarfaWidgetType.INPUT) return false;
            string title = Normalize((w.Title ?? "") + " " + (w.DefinitionName ?? ""));
            return (title.Contains("открыт") && title.Contains("закрыт")) || ContainsAny(title, "состояние", "статус", "положение", "режим");
        }
        private static bool EntityMatches(EntityKind kind, MarfaWidgetState w)
        {
            string s = Normalize((w.Title ?? "") + " " + (w.Page ?? "") + " " + (w.DefinitionName ?? "") + " " + (w.ConfigJson ?? ""));
            return EntityAliases(kind).Item2.Any(s.Contains);
        }
        private static EntityKind DetectEntityKind(string s)
        {
            if (ContainsAny(s,"двер")) return EntityKind.DOOR;
            if (ContainsAny(s,"форточ","фрамуг")) return EntityKind.VENT;
            if (ContainsAny(s,"окн")) return EntityKind.WINDOW;
            if (ContainsAny(s,"ворот")) return EntityKind.GATE;
            if (ContainsAny(s,"насос","помп")) return EntityKind.PUMP;
            if (ContainsAny(s,"вентилят","вент")) return EntityKind.FAN;
            if (ContainsAny(s,"обогрев","отоп","нагрев")) return EntityKind.HEATER;
            if (ContainsAny(s,"клапан","кран")) return EntityKind.VALVE;
            if (ContainsAny(s,"свет","ламп")) return EntityKind.LIGHT;
            if (ContainsAny(s,"полив","орош")) return EntityKind.IRRIGATION;
            return EntityKind.GENERIC;
        }
        private static Tuple<string[],string[]> EntityAliases(EntityKind k)
        {
            switch(k)
            {
                case EntityKind.DOOR: return Tuple.Create(new[]{"двер"},new[]{"двер","вход"});
                case EntityKind.VENT: return Tuple.Create(new[]{"форточ","фрамуг"},new[]{"форточ","фрамуг","окн"});
                case EntityKind.WINDOW: return Tuple.Create(new[]{"окн"},new[]{"окн","форточ"});
                case EntityKind.GATE: return Tuple.Create(new[]{"ворот"},new[]{"ворот"});
                case EntityKind.PUMP: return Tuple.Create(new[]{"насос","помп"},new[]{"насос","помп"});
                case EntityKind.FAN: return Tuple.Create(new[]{"вентилят","вент"},new[]{"вентилят","вент"});
                case EntityKind.HEATER: return Tuple.Create(new[]{"обогрев","отоп","нагрев"},new[]{"обогрев","отоп","нагрев"});
                case EntityKind.VALVE: return Tuple.Create(new[]{"клапан","кран"},new[]{"клапан","кран"});
                case EntityKind.LIGHT: return Tuple.Create(new[]{"свет","ламп"},new[]{"свет","ламп"});
                case EntityKind.IRRIGATION: return Tuple.Create(new[]{"полив","орош"},new[]{"полив","орош"});
                default: return Tuple.Create(new string[0],new string[0]);
            }
        }
        private static string EntityName(EntityKind k) { return k == EntityKind.GENERIC ? "объект" : k.ToString().ToLowerInvariant(); }

        private IList<string> MatchingContextPages(string text, IList<MarfaDevice> devices, string desired)
        {
            var command = ContextTokens(text, devices); if (command.Count == 0) return new List<string>();
            var ranked = new List<Tuple<string,int>>();
            foreach (var page in devices.SelectMany(SafeWidgets).Select(w=>w.Page).Where(p=>!String.IsNullOrWhiteSpace(p)).Distinct())
            {
                string p = Normalize(page); var pageTokens = Tokens(p).Select(RussianSemanticNormalizer.Lemma).ToList();
                var relevant = command.Where(t=>pageTokens.Any(pt=>LexicalMatch(t,pt))).ToList();
                if (relevant.Count == 0) continue;
                int score = relevant.Count * 1000;
                foreach(var c in devices.SelectMany(d=>SafeWidgets(d).Where(w=>Normalize(w.Page)==p).Select(w=>new {Device=d,Widget=w})).Where(x=>IsControllable(x.Widget)))
                    score = Math.Max(score, relevant.Count*1000 + Math.Max(0, ControlScore(text,null,null,c.Device,c.Widget,desired)));
                ranked.Add(Tuple.Create(p,score));
            }
            if (ranked.Count == 0) return new List<string>();
            int best=ranked.Max(x=>x.Item2);
            return ranked.Where(x=>x.Item2==best).Select(x=>x.Item1).ToList();
        }
        private static IList<string> UnknownContextWords(string text, IList<MarfaDevice> devices)
        {
            var context=ContextTokens(text,devices); var pages=new HashSet<string>(devices.SelectMany(SafeWidgets).SelectMany(w=>Tokens(Normalize(w.Page))).Select(RussianSemanticNormalizer.Lemma));
            return context.Where(t=>!IsSelectionToken(t) && !pages.Any(p=>LexicalMatch(t,p))).Distinct().ToList();
        }
        private static IList<string> ContextTokens(string text, IList<MarfaDevice> devices)
        {
            var skip=new HashSet<string>(new[]{"открой","открыть","открывай","закрой","закрыть","закрывай","включи","включить","выключи","выключить","запусти","останови","покажи","показать","скажи","узнай","марфа","марфу","на","в","во","и","а","по","к","для","с","со","если","когда","сейчас","потом","позже","через","час","часа","часов","минут","минуту","минуты","секунд","градус","градуса","температура","температуры","температур","влажность","влажности","давление","давления","датчик","датчики","реле","выход","кнопка","кнопки","gpio","канал","состояние","статус","положение","открыт","закрыт","какая","какой","какое","какие","сколько","номер","вкладка","вкладке","страница","странице"});
            var ids=new HashSet<string>(devices.SelectMany(SafeWidgets).Select(w=>Normalize(w.Id)));
            return Tokens(EmojiSemanticText.Normalize(text)).Where(t=>t.Length>=2 && !skip.Contains(t) && !Regex.IsMatch(t,@"^\d+$") && !ids.Contains(Normalize(t)))
                .Select(RussianSemanticNormalizer.Lemma).Where(t=>t.Length>0).Distinct().ToList();
        }
        private static bool IsSelectionToken(string t) { return Regex.IsMatch(t,@"^\d+$") || ContainsAny(t,"перв","втор","трет","четвер","пят","один","два","две","три","четыре","пять","шесть","семь","восемь","девять","десять"); }
        private static bool LexicalMatch(string a,string b) { return a==b || (a.Length>=5 && b.Length>=5 && (a.StartsWith(b) || b.StartsWith(a))); }
        private static bool ContainsSemanticToken(string text,string key) { return Tokens(text).Select(RussianSemanticNormalizer.Lemma).Any(t=>LexicalMatch(t,key)); }
        private static IList<string> Tokens(string s) { return Regex.Split(s??"",@"\s+").Where(t=>t.Length>0).ToList(); }
        private static string Normalize(string s) { return RussianSemanticNormalizer.Normalize(s??""); }
        private static bool ContainsAny(string text, params string[] words) { return words.Any(w=>(text??"").Contains(w)); }
        private static IEnumerable<MarfaWidgetState> SafeWidgets(MarfaDevice d) { return d.Widgets ?? new List<MarfaWidgetState>(); }
        private static string ClarificationForControls(IList<ControlCandidate> c) { return "Уточните, что именно выбрать: " + String.Join(" или ", c.Take(5).Select(x=>(String.IsNullOrWhiteSpace(x.Widget.Title)?x.Widget.Id:x.Widget.Title)+(String.IsNullOrWhiteSpace(x.Widget.Page)?"":" (вкладка "+x.Widget.Page+")")).Distinct()); }
        private static string ClarificationForSensors(IList<SensorCandidate> c) { return "Уточните, какой датчик использовать: " + String.Join(" или ", c.Take(5).Select(x=>(String.IsNullOrWhiteSpace(x.Widget.Title)?x.Widget.Id:x.Widget.Title)+(String.IsNullOrWhiteSpace(x.Widget.Page)?"":" (вкладка "+x.Widget.Page+")")).Distinct()); }
        private static string AvailablePageNames(IList<MarfaDevice> d) { var pages=d.SelectMany(SafeWidgets).Select(w=>w.Page).Where(p=>!String.IsNullOrWhiteSpace(p)).Distinct().Take(8).ToList(); return pages.Count==0?"название вкладки не передано":String.Join(", ",pages); }
        private static string PrettyPage(string p,IList<MarfaDevice>d) { return d.SelectMany(SafeWidgets).Select(w=>w.Page).FirstOrDefault(x=>Normalize(x)==p)??p; }
        private static string ExplicitElementId(string text,IList<MarfaDevice>d) { var tokens=Tokens(text); return d.SelectMany(SafeWidgets).Select(w=>w.Id).Where(id=>!String.IsNullOrWhiteSpace(id)).Distinct().OrderByDescending(id=>id.Length).FirstOrDefault(id=>tokens.Any(t=>String.Equals(t,id,StringComparison.OrdinalIgnoreCase))); }
    }
}