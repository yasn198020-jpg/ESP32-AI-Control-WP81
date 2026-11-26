using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Voice
{
    public sealed class MarfaAnalyticalCandidate
    {
        public WidgetState Widget { get; set; }
        public int Score { get; set; }
        public IList<string> Reasons { get; set; }
    }

    public sealed class MarfaAnalyticalResolution
    {
        public MarfaAnalyticalCandidate Candidate { get; set; }
        public IList<MarfaAnalyticalCandidate> Candidates { get; set; }
        public string Clarification { get; set; }

        public MarfaAnalyticalResolution()
        {
            Candidates = new List<MarfaAnalyticalCandidate>();
        }
    }

    /// <summary>
    /// WP81 port of Marfa's deterministic analytical resolver. Element IDs are
    /// authoritative; page, semantic entity and widget role are hard constraints.
    /// </summary>
    public sealed class MarfaAnalyticalEngine
    {
        private enum EntityKind { Door, Vent, Window, Gate, Pump, Fan, Heater, Valve, Light, Irrigation, Generic }
        private enum SensorKind { Temperature, Humidity, Pressure, Value }

        public MarfaAnalyticalResolution ResolveControl(string text, IList<WidgetState> widgets, string desiredValue)
        {
            var normalized = RussianSemanticNormalizer.Normalize(text);
            var page = ExplicitPage(normalized, widgets);
            if (HasExplicitPageMarker(normalized) && page == null)
                return new MarfaAnalyticalResolution
                {
                    Clarification = "Не нашла указанную вкладку. Доступные вкладки: " + AvailablePages(widgets) + "."
                };
            var exactId = ExplicitElementId(normalized, widgets);
            var all = new List<MarfaAnalyticalCandidate>();

            foreach (var widget in widgets ?? new List<WidgetState>())
            {
                if (widget == null || !IsControllable(widget)) continue;
                if (exactId != null && !string.Equals(widget.Id, exactId, StringComparison.OrdinalIgnoreCase)) continue;
                var score = ControlScore(normalized, page, widget, desiredValue);
                if (score > 0 || page != null || exactId != null)
                    all.Add(new MarfaAnalyticalCandidate
                    {
                        Widget = widget,
                        Score = score,
                        Reasons = ControlReasons(normalized, page, widget, desiredValue)
                    });
            }

            if (exactId != null)
            {
                if (all.Count == 1)
                    return new MarfaAnalyticalResolution { Candidate = all[0], Candidates = all };
                return new MarfaAnalyticalResolution
                {
                    Candidates = all,
                    Clarification = all.Count > 1
                        ? "ID элемента «" + exactId + "» найден несколько раз. Нужен уникальный ID."
                        : "Элемент с ID «" + exactId + "» не найден."
                };
            }

            var contextPages = page == null ? MatchingContextPages(normalized, widgets) : new List<string>();
            if (page == null && contextPages.Count == 0 && !HasNonLexicalPage(widgets))
            {
                var unknownContext = UnknownContextWords(normalized, widgets);
                if (unknownContext.Count > 0)
                    return new MarfaAnalyticalResolution
                    {
                        Clarification = "Я нашла объект, но не нашла совпадение контекста «" +
                            string.Join(", ", unknownContext.ToArray()) + "». Уточните вкладку. Доступные вкладки: " +
                            AvailablePages(widgets) + "."
                    };
            }
            if (page != null)
                all = all.Where(c => RussianSemanticNormalizer.Normalize(c.Widget.Page).Trim() == page).ToList();
            else if (contextPages.Count == 1)
                all = all.Where(c => RussianSemanticNormalizer.Normalize(c.Widget.Page).Trim() == contextPages[0]).ToList();

            var contextTokens = ContextTokens(normalized, widgets);
            if (contextTokens.Count > 0)
            {
                var scoped = all.Where(candidate =>
                {
                    var corpus = RussianSemanticNormalizer.Normalize(
                        (candidate.Widget.Description ?? string.Empty) + " " +
                        (candidate.Widget.Page ?? string.Empty) + " " +
                        (candidate.Widget.DeviceId ?? string.Empty) + " " +
                        (candidate.Widget.Id ?? string.Empty));
                    return contextTokens.All(token => ContainsSemanticToken(corpus, token));
                }).ToList();
                if (scoped.Count == 0)
                    return new MarfaAnalyticalResolution
                    {
                        Clarification = "Не нашла совпадение контекста «" +
                            string.Join(", ", contextTokens.ToArray()) + "». Уточните вкладку или объект."
                    };
                all = scoped;
            }

            var entity = DetectEntity(normalized);
            if (entity != EntityKind.Generic)
            {
                // A spoken entity is a hard constraint. Never silently redirect
                // "дверь" to an unrelated relay or generic button.
                all = all.Where(c => EntityMatches(entity, c.Widget)).ToList();
            }

            var explicitPhysical = HasAny(normalized, "реле", "выход", "выходной", "кнопк", "gpio", "канал", "исполнитель");
            if (!explicitPhysical)
            {
                var logical = all.Where(c => IsLogicalStateWidget(c.Widget)).ToList();
                if (logical.Count > 0) all = logical;
            }

            all = all.OrderByDescending(c => c.Score)
                .ThenBy(c => c.Widget.Order)
                .ThenBy(c => RussianSemanticNormalizer.Normalize(c.Widget.Page))
                .ThenBy(c => c.Widget.Id).ToList();

            if (all.Count == 0)
            {
                var suffix = page == null ? string.Empty : " на вкладке «" + OriginalPage(page, widgets) + "»";
                return new MarfaAnalyticalResolution
                {
                    Clarification = "Я не нашла однозначный управляемый элемент" + suffix +
                        ". Назовите объект, ID элемента или вкладку."
                };
            }

            var best = all[0];
            var second = all.Count > 1 ? all[1] : null;
            var contextResolved = page != null || contextPages.Count == 1;
            if (all.Count == 1 && contextResolved)
                return new MarfaAnalyticalResolution { Candidate = best, Candidates = all };

            var tied = all.Count(c => c.Score == best.Score) > 1;
            var weak = best.Score < 22;
            var tooClose = second != null && second.Score >= 18 && best.Score - second.Score < 5;
            if (tied || weak || tooClose)
                return new MarfaAnalyticalResolution
                {
                    Candidates = all.Take(5).ToList(),
                    Clarification = BuildControlClarification(all)
                };

            return new MarfaAnalyticalResolution { Candidate = best, Candidates = all };
        }

        public MarfaAnalyticalResolution ResolveSensor(string text, IList<WidgetState> widgets)
        {
            var normalized = RussianSemanticNormalizer.Normalize(text);
            var page = ExplicitPage(normalized, widgets);
            if (HasExplicitPageMarker(normalized) && page == null)
                return new MarfaAnalyticalResolution
                {
                    Clarification = "Не нашла указанную вкладку. Доступные вкладки: " + AvailablePages(widgets) + "."
                };
            var kind = DetectSensorKind(normalized);
            if (page == null && !HasNonLexicalPage(widgets))
            {
                var contextPages = MatchingContextPages(normalized, widgets);
                if (contextPages.Count == 0)
                {
                    var unknownContext = UnknownContextWords(normalized, widgets);
                    if (unknownContext.Count > 0)
                        return new MarfaAnalyticalResolution
                        {
                            Clarification = "Я нашла датчик, но не нашла совпадение контекста «" +
                                string.Join(", ", unknownContext.ToArray()) + "». Уточните вкладку. Доступные вкладки: " +
                                AvailablePages(widgets) + "."
                        };
                }
            }
            var all = new List<MarfaAnalyticalCandidate>();
            foreach (var widget in widgets ?? new List<WidgetState>())
            {
                if (widget == null || !IsSensor(widget)) continue;
                var score = SensorScore(normalized, kind, widget);
                if (score > 0 || page != null)
                    all.Add(new MarfaAnalyticalCandidate
                    {
                        Widget = widget,
                        Score = score,
                        Reasons = new List<string>()
                    });
            }
            var contextPagesForSensor = page == null ? MatchingContextPages(normalized, widgets) : new List<string>();
            if (page != null)
                all = all.Where(c => RussianSemanticNormalizer.Normalize(c.Widget.Page).Trim() == page).ToList();
            else if (contextPagesForSensor.Count == 1)
                all = all.Where(c => RussianSemanticNormalizer.Normalize(c.Widget.Page).Trim() == contextPagesForSensor[0]).ToList();

            var sensorContextTokens = ContextTokens(normalized, widgets);
            if (sensorContextTokens.Count > 0)
            {
                var scoped = all.Where(candidate =>
                {
                    var corpus = RussianSemanticNormalizer.Normalize(
                        (candidate.Widget.Description ?? string.Empty) + " " +
                        (candidate.Widget.Page ?? string.Empty) + " " +
                        (candidate.Widget.DeviceId ?? string.Empty) + " " +
                        (candidate.Widget.Id ?? string.Empty));
                    return sensorContextTokens.All(token => ContainsSemanticToken(corpus, token));
                }).ToList();
                if (scoped.Count == 0)
                    return new MarfaAnalyticalResolution
                    {
                        Clarification = "Я нашла датчик, но не нашла совпадение контекста «" +
                            string.Join(", ", sensorContextTokens.ToArray()) + "». Уточните вкладку или объект."
                    };
                all = scoped;
            }

            if (all.Count == 0)
                return new MarfaAnalyticalResolution
                {
                    Clarification = kind == SensorKind.Temperature ? "Какую температуру показать? Укажите объект или вкладку." :
                        kind == SensorKind.Humidity ? "Какую влажность показать? Укажите объект или вкладку." :
                        kind == SensorKind.Pressure ? "Какое давление показать? Укажите объект или вкладку." :
                        "Какой датчик показать? Назовите его или укажите вкладку."
                };

            all = all.OrderByDescending(c => c.Score)
                .ThenBy(c => RussianSemanticNormalizer.Normalize(c.Widget.Page))
                .ThenBy(c => c.Widget.Order)
                .ThenBy(c => c.Widget.Id).ToList();
            var best = all[0];
            var second = all.Count > 1 ? all[1] : null;
            if (all.Count == 1 && page != null)
                return new MarfaAnalyticalResolution { Candidate = best, Candidates = all };

            var tied = all.Count(c => c.Score == best.Score) > 1;
            var weak = best.Score < 16;
            var tooClose = second != null && second.Score >= 14 && best.Score - second.Score < 4;
            if (tied || weak || tooClose)
                return new MarfaAnalyticalResolution
                {
                    Candidates = all.Take(5).ToList(),
                    Clarification = BuildSensorClarification(all)
                };
            return new MarfaAnalyticalResolution { Candidate = best, Candidates = all };
        }

        private static List<string> ContextTokens(string text, IList<WidgetState> widgets)
        {
            var ignored = new HashSet<string>(new[]
            {
                "включить", "выключить", "открыть", "закрыть", "запустить", "остановить",
                "показать", "сказать", "узнать", "марфа", "пожалуйста", "через", "спустя",
                "на", "в", "во", "по", "для", "из", "с", "со", "к", "у", "и", "а",
                "двер", "форточк", "окн", "ворот", "насос", "вентилятор", "вент",
                "обогрев", "отоп", "кран", "клапан", "свет", "ламп", "полив", "рел",
                "кнопк", "канал", "выход", "gpio", "автомат", "управлен",
                "температур", "темп", "влажност", "давлен", "датчик", "значен",
                "какая", "какое", "какие", "какую", "какой", "сколько", "покажи",
                "показать", "скажи", "сказать", "узнай", "узнать", "что", "там", "здесь",
                "если", "когда", "при", "условии", "выше", "больше", "ниже", "меньше",
                "градус", "градуса", "градусов", "номер", "состояние", "статус",
                "час", "часа", "часов", "ч", "минут", "минута", "минуты", "мин",
                "секунд", "секунда", "секунды", "сек"
            }, StringComparer.OrdinalIgnoreCase);
            var knownIds = new HashSet<string>((widgets ?? new List<WidgetState>())
                .Select(w => RussianSemanticNormalizer.Normalize(w.Id)), StringComparer.OrdinalIgnoreCase);
            return RussianSemanticNormalizer.Tokens(text)
                .Where(token => token.Length >= 3 && !ignored.Contains(token) &&
                    !knownIds.Contains(token) && !Regex.IsMatch(token, @"^\d+$"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool HasNonLexicalPage(IList<WidgetState> widgets)
        {
            return (widgets ?? new List<WidgetState>()).Any(w =>
                !string.IsNullOrWhiteSpace(w.Page) &&
                RussianSemanticNormalizer.Normalize(w.Page).Length == 0 &&
                HasNonLetterDigitOrWhitespace(w.Page));
        }

        private static bool HasNonLetterDigitOrWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (var i = 0; i < value.Length; i++)
            {
                if (!char.IsLetterOrDigit(value[i]) && !char.IsWhiteSpace(value[i]))
                    return true;
            }
            return false;
        }

        private static List<string> UnknownContextWords(string text, IList<WidgetState> widgets)
        {
            var ignored = new HashSet<string>(new[]
            {
                "включить", "выключить", "открыть", "закрыть", "запустить", "остановить",
                "показать", "сказать", "марфа", "пожалуйста", "через", "спустя",
                "на", "в", "во", "по", "для", "из", "с", "со", "к", "у", "и", "а",
                "двер", "форточк", "окн", "ворот", "насос", "вентилятор", "вент",
                "обогрев", "отоп", "кран", "клапан", "свет", "ламп", "полив", "рел",
                "кнопк", "канал", "выход", "gpio", "автомат", "управлен",
                "температур", "темп", "влажност", "давлен", "датчик", "значен",
                "какая", "какое", "какие", "какую", "какой", "сколько", "покажи",
                "показать", "скажи", "сказать", "узнай", "узнать", "что", "там", "здесь",
                "если", "когда", "при", "условии", "выше", "больше", "ниже", "меньше",
                "градус", "градуса", "градусов", "номер", "состояние", "статус"
            }, StringComparer.OrdinalIgnoreCase);

            var pageTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var widget in widgets ?? new List<WidgetState>())
                foreach (var token in RussianSemanticNormalizer.Tokens(
                    (widget.Page ?? string.Empty) + " " +
                    (widget.Description ?? string.Empty) + " " +
                    (widget.DeviceId ?? string.Empty) + " " +
                    (widget.DefinitionName ?? string.Empty)))
                    pageTokens.Add(token);

            var result = new List<string>();
            foreach (var token in RussianSemanticNormalizer.Tokens(text))
            {
                if (token.Length < 3 || ignored.Contains(token) || Regex.IsMatch(token, @"^\d+$") ||
                    IsSelectionToken(token))
                    continue;
                var knownId = (widgets ?? new List<WidgetState>()).Any(w =>
                    string.Equals(RussianSemanticNormalizer.Normalize(w.Id), token, StringComparison.OrdinalIgnoreCase));
                if (knownId) continue;
                var knownPage = pageTokens.Any(pageToken =>
                    token == pageToken ||
                    (token.Length >= 5 && pageToken.Length >= 5 &&
                     (token.StartsWith(pageToken, StringComparison.Ordinal) ||
                      pageToken.StartsWith(token, StringComparison.Ordinal))));
                if (!knownPage && !result.Contains(token, StringComparer.OrdinalIgnoreCase))
                    result.Add(token);
            }
            return result;
        }

        private static bool IsSelectionToken(string token)
        {
            if (Regex.IsMatch(token ?? string.Empty, @"^\d+$")) return true;
            var forms = new[]
            {
                "ноль", "один", "одна", "одно", "два", "две", "три", "четыре",
                "пять", "шесть", "семь", "восемь", "девять", "десять",
                "первый", "первая", "первое", "первую", "второй", "вторая",
                "второе", "вторую", "третий", "третья", "третье", "третью",
                "четвертый", "четвертая", "четвертую", "пятый", "пятая", "пятое"
            };
            return forms.Any(form => RussianSemanticNormalizer.NormalizeToken(form) == token);
        }

        private static List<string> MatchingContextPages(string text, IList<WidgetState> widgets)
        {
            var ignored = new HashSet<string>(new[]
            {
                "включить", "выключить", "открыть", "закрыть", "запустить", "остановить",
                "показать", "сказать", "марфа", "пожалуйста", "через", "спустя",
                "на", "в", "во", "по", "для", "из", "с", "со", "к", "у", "и", "а",
                "дверь", "форточка", "окно", "ворота", "насос", "вентилятор",
                "обогрев", "кран", "клапан", "свет", "лампа", "полив", "реле",
                "кнопка", "канал", "выход", "gpio", "автомат", "управление"
            }, StringComparer.OrdinalIgnoreCase);
            var spoken = RussianSemanticNormalizer.Tokens(text)
                .Where(t => t.Length >= 2 && !ignored.Contains(t) && !Regex.IsMatch(t, @"^\d+$"))
                .ToList();
            if (spoken.Count == 0) return new List<string>();

            var pages = (widgets ?? new List<WidgetState>())
                .Select(w => RussianSemanticNormalizer.Normalize(w.Page).Trim())
                .Where(p => p.Length > 0).Distinct().ToList();
            var ranked = new List<Tuple<string, int>>();
            foreach (var candidatePage in pages)
            {
                var pageTokens = RussianSemanticNormalizer.Tokens(candidatePage);
                var score = spoken.Count(token => pageTokens.Any(pageToken =>
                    token == pageToken ||
                    (token.Length >= 5 && pageToken.Length >= 5 &&
                     (token.StartsWith(pageToken, StringComparison.Ordinal) ||
                      pageToken.StartsWith(token, StringComparison.Ordinal)))));
                if (score > 0) ranked.Add(Tuple.Create(candidatePage, score));
            }
            if (ranked.Count == 0) return new List<string>();
            var best = ranked.Max(p => p.Item2);
            return ranked.Where(p => p.Item2 == best).Select(p => p.Item1).ToList();
        }

        private static int ControlScore(string text, string page, WidgetState widget, string desiredValue)
        {
            var title = RussianSemanticNormalizer.Normalize(widget.Description);
            var widgetPage = RussianSemanticNormalizer.Normalize(widget.Page);
            var deviceName = RussianSemanticNormalizer.Normalize(widget.DeviceId);
            var score = 0;
            var entity = DetectEntity(text);
            var aliases = EntityAliases(entity);
            if (aliases.Item1.Any(text.Contains))
            {
                if (aliases.Item2.Any(title.Contains)) score += 34;
                if (aliases.Item2.Any(widgetPage.Contains)) score += 10;
                if (aliases.Item2.Any(deviceName.Contains)) score += 8;
            }

            foreach (var token in RussianSemanticNormalizer.Tokens(text))
            {
                if (token.Length < 3) continue;
                if (ContainsSemanticToken(title, token)) score += 6;
                if (ContainsSemanticToken(widgetPage, token)) score += 3;
                if (ContainsSemanticToken(deviceName, token)) score += 2;
            }

            var explicitPhysical = HasAny(text, "реле", "выход", "выходной", "кнопк", "gpio", "канал", "исполнитель");
            var hasOpen = HasAny(title, "открыть", "открой", "открыва", "распах", "поднять", "подъем");
            var hasClose = HasAny(title, "закрыть", "закрой", "закрыва", "опустить", "опуск");
            var indicator = (title.Contains("открыт") && title.Contains("закрыт")) ||
                HasAny(title, "состояние", "статус", "индикатор", "положение");

            if (desiredValue == "1")
                score += (hasOpen ? 40 : 0) + (hasClose && !hasOpen ? -18 : 0) +
                    (indicator && !explicitPhysical ? 60 : 0) + (explicitPhysical && !indicator ? 45 : 0);
            else if (desiredValue == "0")
                score += (hasClose ? 40 : 0) + (hasOpen && !hasClose ? -18 : 0) +
                    (indicator && !explicitPhysical ? 60 : 0) + (explicitPhysical && !indicator ? 45 : 0);
            else if (indicator && !explicitPhysical) score += 12;

            if (page != null && widgetPage != page) return -100000;
            if (page != null) score += 500;
            return score;
        }

        private static int SensorScore(string text, SensorKind kind, WidgetState widget)
        {
            var title = RussianSemanticNormalizer.Normalize(widget.Description);
            var page = RussianSemanticNormalizer.Normalize(widget.Page);
            var device = RussianSemanticNormalizer.Normalize(widget.DeviceId);
            var score = 0;
            if (kind == SensorKind.Temperature)
            {
                if (HasAny(title, "температур", "темп")) score += 22;
                if ((widget.Unit ?? string.Empty).Contains("°") || string.Equals(widget.Unit, "c", StringComparison.OrdinalIgnoreCase)) score += 10;
            }
            else if (kind == SensorKind.Humidity)
            {
                if (title.Contains("влажн")) score += 22;
                if ((widget.Unit ?? string.Empty).Contains("%")) score += 7;
            }
            else if (kind == SensorKind.Pressure)
            {
                if (title.Contains("давлен")) score += 22;
                var unit = (widget.Unit ?? string.Empty).ToLowerInvariant();
                if (unit.Contains("па") || unit.Contains("bar")) score += 7;
            }

            foreach (var token in RussianSemanticNormalizer.Tokens(text))
            {
                if (token.Length < 3) continue;
                if (ContainsSemanticToken(title, token)) score += 8;
                if (ContainsSemanticToken(page, token)) score += 5;
                if (ContainsSemanticToken(device, token)) score += 3;
            }
            if (RussianSemanticNormalizer.Tokens(text).Any(t => string.Equals(t, widget.Id, StringComparison.OrdinalIgnoreCase))) score += 1000;
            return score;
        }

        private static SensorKind DetectSensorKind(string text)
        {
            if (HasAny(text, "температур", "темп", "градус", "жарко", "холодно")) return SensorKind.Temperature;
            if (HasAny(text, "влажност", "влажн")) return SensorKind.Humidity;
            if (HasAny(text, "давлен")) return SensorKind.Pressure;
            return SensorKind.Value;
        }

        private static bool IsControllable(WidgetState widget)
        {
            var type = RussianSemanticNormalizer.Normalize((widget.WidgetType ?? "") + " " + (widget.DefinitionName ?? ""));
            return HasAny(type, "toggle", "button", "input", "slider", "select", "dropdown", "btn", "vbtn", "switch", "relay");
        }

        private static bool IsSensor(WidgetState widget)
        {
            var type = RussianSemanticNormalizer.Normalize((widget.WidgetType ?? "") + " " + (widget.DefinitionName ?? ""));
            return HasAny(type, "value", "status", "sensor", "temperature", "humidity") ||
                !string.IsNullOrWhiteSpace(widget.Unit);
        }

        private static bool IsLogicalStateWidget(WidgetState widget)
        {
            var type = RussianSemanticNormalizer.Normalize(widget.WidgetType);
            if (!HasAny(type, "toggle", "input")) return false;
            var title = RussianSemanticNormalizer.Normalize((widget.Description ?? "") + " " + (widget.DefinitionName ?? ""));
            return (title.Contains("открыт") && title.Contains("закрыт")) ||
                HasAny(title, "состояние", "статус", "положение", "режим");
        }

        private static bool EntityMatches(EntityKind kind, WidgetState widget)
        {
            var corpus = RussianSemanticNormalizer.Normalize((widget.Description ?? "") + " " +
                (widget.Page ?? "") + " " + (widget.DefinitionName ?? "") + " " + (widget.ConfigJson ?? ""));
            var aliases = EntityAliases(kind).Item2;
            if (aliases.Any(corpus.Contains)) return true;
            switch (kind)
            {
                case EntityKind.Door: return HasAny(corpus, "двер", "вход");
                case EntityKind.Vent: return HasAny(corpus, "форточ", "фрамуг", "окн");
                case EntityKind.Window: return HasAny(corpus, "окн", "форточ");
                case EntityKind.Gate: return corpus.Contains("ворот");
                case EntityKind.Pump: return HasAny(corpus, "насос", "помп");
                case EntityKind.Fan: return HasAny(corpus, "вентилят", "вент");
                case EntityKind.Heater: return HasAny(corpus, "обогрев", "отоп", "нагрев");
                case EntityKind.Valve: return HasAny(corpus, "клапан", "кран");
                case EntityKind.Light: return HasAny(corpus, "свет", "ламп");
                case EntityKind.Irrigation: return HasAny(corpus, "полив", "орош");
                default: return true;
            }
        }

        private static EntityKind DetectEntity(string text)
        {
            if (HasAny(text, "двер")) return EntityKind.Door;
            if (HasAny(text, "форточ", "фрамуг")) return EntityKind.Vent;
            if (HasAny(text, "окн")) return EntityKind.Window;
            if (HasAny(text, "ворот")) return EntityKind.Gate;
            if (HasAny(text, "насос", "помп")) return EntityKind.Pump;
            if (HasAny(text, "вентилят", "вент")) return EntityKind.Fan;
            if (HasAny(text, "обогрев", "отоп", "нагрев")) return EntityKind.Heater;
            if (HasAny(text, "клапан", "кран")) return EntityKind.Valve;
            if (HasAny(text, "свет", "ламп")) return EntityKind.Light;
            if (HasAny(text, "полив", "орош")) return EntityKind.Irrigation;
            return EntityKind.Generic;
        }

        private static Tuple<string[], string[]> EntityAliases(EntityKind kind)
        {
            switch (kind)
            {
                case EntityKind.Door: return Tuple.Create(new[] { "двер" }, new[] { "двер", "вход" });
                case EntityKind.Vent: return Tuple.Create(new[] { "форточ", "фрамуг" }, new[] { "форточ", "фрамуг", "окн" });
                case EntityKind.Window: return Tuple.Create(new[] { "окн" }, new[] { "окн", "форточ" });
                case EntityKind.Gate: return Tuple.Create(new[] { "ворот" }, new[] { "ворот" });
                case EntityKind.Pump: return Tuple.Create(new[] { "насос", "помп" }, new[] { "насос", "помп" });
                case EntityKind.Fan: return Tuple.Create(new[] { "вентилят", "вент" }, new[] { "вентилят", "вент" });
                case EntityKind.Heater: return Tuple.Create(new[] { "обогрев", "отоп", "нагрев" }, new[] { "обогрев", "отоп", "нагрев" });
                case EntityKind.Valve: return Tuple.Create(new[] { "клапан", "кран" }, new[] { "клапан", "кран" });
                case EntityKind.Light: return Tuple.Create(new[] { "свет", "ламп" }, new[] { "свет", "ламп" });
                case EntityKind.Irrigation: return Tuple.Create(new[] { "полив", "орош" }, new[] { "полив", "орош" });
                default: return Tuple.Create(new string[0], new string[0]);
            }
        }

        private static List<string> ControlReasons(string text, string page, WidgetState widget, string desiredValue)
        {
            var reasons = new List<string>();
            if (DetectEntity(text) != EntityKind.Generic) reasons.Add(DetectEntity(text).ToString());
            if (page != null && RussianSemanticNormalizer.Normalize(widget.Page) == page) reasons.Add("вкладка " + widget.Page);
            if (!string.IsNullOrWhiteSpace(desiredValue)) reasons.Add("значение " + desiredValue);
            if (!string.IsNullOrWhiteSpace(widget.Description)) reasons.Add("название " + widget.Description);
            return reasons;
        }

        private static bool HasExplicitPageMarker(string text)
        {
            return Regex.IsMatch(text, @"(?:на\s+страниц(?:е|у)|на\s+вкладк(?:е|у)|во\s+вкладк(?:е|у)|в\s+вкладк(?:е|у)|страниц(?:а|у)|вкладк(?:а|у))\s+");
        }

        private static string AvailablePages(IList<WidgetState> widgets)
        {
            var pages = (widgets ?? new List<WidgetState>())
                .Select(w => w.Page)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8).ToList();
            return pages.Count == 0 ? "нет переданных вкладок" : string.Join(", ", pages.ToArray());
        }

        private static string ExplicitPage(string text, IList<WidgetState> widgets)
        {
            var marker = Regex.Match(text, @"(?:на\s+страниц(?:е|у)|на\s+вкладк(?:е|у)|во\s+вкладк(?:е|у)|в\s+вкладк(?:е|у)|страниц(?:а|у)|вкладк(?:а|у))\s+");
            if (!marker.Success) return null;
            var tail = " " + text.Substring(marker.Index + marker.Length).Trim() + " ";
            return (widgets ?? new List<WidgetState>())
                .Select(w => RussianSemanticNormalizer.Normalize(w.Page).Trim())
                .Where(p => p.Length > 0).Distinct()
                .OrderByDescending(p => p.Length)
                .FirstOrDefault(p => tail.Contains(" " + p + " "));
        }

        private static string ExplicitElementId(string text, IList<WidgetState> widgets)
        {
            var tokens = RussianSemanticNormalizer.Tokens(text);
            return (widgets ?? new List<WidgetState>())
                .Select(w => (w.Id ?? string.Empty).Trim())
                .Where(id => id.Length > 0).Distinct()
                .OrderByDescending(id => id.Length)
                .FirstOrDefault(id => tokens.Any(t => string.Equals(t, id, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool ContainsSemanticToken(string text, string key)
        {
            return RussianSemanticNormalizer.Tokens(text).Any(token =>
                token == key || (token.Length >= 5 && key.Length >= 5 &&
                (token.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(token, StringComparison.Ordinal))));
        }

        private static bool HasAny(string text, params string[] values)
        {
            foreach (var value in values)
                if ((text ?? string.Empty).IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string BuildControlClarification(IEnumerable<MarfaAnalyticalCandidate> candidates)
        {
            var names = candidates.Take(5).Select(c =>
                (string.IsNullOrWhiteSpace(c.Widget.Description) ? c.Widget.Id : c.Widget.Description) +
                (string.IsNullOrWhiteSpace(c.Widget.Page) ? string.Empty : " (вкладка " + c.Widget.Page + ")"))
                .Distinct().ToList();
            return names.Count == 0 ? "Уточните, чем именно управлять." :
                "Уточните, что именно выбрать: " + string.Join(" или ", names);
        }

        private static string BuildSensorClarification(IEnumerable<MarfaAnalyticalCandidate> candidates)
        {
            var names = candidates.Take(5).Select(c =>
                (string.IsNullOrWhiteSpace(c.Widget.Description) ? c.Widget.Id : c.Widget.Description) +
                (string.IsNullOrWhiteSpace(c.Widget.Page) ? string.Empty : " (вкладка " + c.Widget.Page + ")"))
                .Distinct().ToList();
            return names.Count == 0 ? "Уточните, какой датчик нужен." :
                "Уточните, какой датчик использовать: " + string.Join(" или ", names);
        }

        private static string OriginalPage(string normalized, IList<WidgetState> widgets)
        {
            var match = (widgets ?? new List<WidgetState>())
                .FirstOrDefault(w => RussianSemanticNormalizer.Normalize(w.Page).Trim() == normalized);
            return match == null ? normalized : match.Page;
        }
    }
}