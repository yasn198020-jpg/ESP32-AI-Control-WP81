using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    // Platform-neutral AST for the IoTManager scenario language.
    public abstract class IoTExpr
    {
        public sealed class NumberLiteral : IoTExpr { public double Value; public NumberLiteral(double value) { Value = value; } }
        public sealed class StringLiteral : IoTExpr { public string Value; public StringLiteral(string value) { Value = value; } }
        public sealed class Variable : IoTExpr { public string Name; public Variable(string name) { Name = name; } }
        public sealed class Unary : IoTExpr { public string Operator; public IoTExpr Expression; public Unary(string op, IoTExpr e) { Operator = op; Expression = e; } }
        public sealed class Binary : IoTExpr { public string Operator; public IoTExpr Left; public IoTExpr Right; public Binary(string op, IoTExpr l, IoTExpr r) { Operator = op; Left = l; Right = r; } }
        public sealed class Call : IoTExpr { public string Name; public IList<IoTExpr> Arguments; public Call(string name, IList<IoTExpr> args) { Name = name; Arguments = args; } }
    }

    public abstract class IoTStatement
    {
        public sealed class If : IoTStatement { public IoTExpr Condition; public IoTStatement ThenBranch; public IoTStatement ElseBranch; public If(IoTExpr c, IoTStatement t, IoTStatement e) { Condition = c; ThenBranch = t; ElseBranch = e; } }
        public sealed class Assignment : IoTStatement { public string Target; public string Operator; public IoTExpr Expression; public Assignment(string t, string op, IoTExpr e) { Target = t; Operator = op; Expression = e; } }
        public sealed class ExpressionStatement : IoTStatement { public IoTExpr Expression; public ExpressionStatement(IoTExpr e) { Expression = e; } }
        public sealed class Block : IoTStatement { public IList<IoTStatement> Statements; public Block(IList<IoTStatement> s) { Statements = s; } }
    }

    public sealed class IoTScenarioParseResult
    {
        public IList<IoTStatement> Statements { get; set; }
        public IList<string> Errors { get; set; }
        public ISet<string> ReferencedIdentifiers { get; set; }
    }

    public sealed class DeviceScenarioCondition
    {
        public IoTExpr Expression { get; set; }
        public string Rendered { get; set; }
        public ISet<string> Identifiers { get; set; }
    }
    public sealed class DeviceScenarioAction
    {
        public string TargetId { get; set; }
        public string Operator { get; set; }
        public IoTExpr Expression { get; set; }
        public string Rendered { get; set; }
    }
    public sealed class DeviceScenarioRule
    {
        public int Index { get; set; }
        public DeviceScenarioCondition Condition { get; set; }
        public IList<DeviceScenarioAction> Actions { get; set; }
    }
    public sealed class DeviceScenarioModel
    {
        public IList<DeviceScenarioRule> Rules { get; set; }
        public ISet<string> Identifiers { get; set; }
        public IList<string> ParserErrors { get; set; }
        public IList<IoTStatement> Statements { get; set; }
        public bool Valid { get { return ParserErrors != null && ParserErrors.Count == 0 && Rules != null && Rules.Count > 0; } }
    }

    internal enum IoTTokenType
    {
        Identifier, Number, String, If, Then, Else, LeftParen, RightParen, LeftBrace, RightBrace,
        Comma, Semicolon, Assign, SilentAssign, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
        Plus, Minus, Multiply, Divide, BitAnd, BitOr, Bang, End
    }
    internal sealed class IoTToken
    {
        public IoTTokenType Type; public string Text; public int Position;
        public IoTToken(IoTTokenType type, string text, int position) { Type = type; Text = text; Position = position; }
    }

    internal static class IoTScenarioLexer
    {
        public static Tuple<List<IoTToken>, List<string>> Tokenize(string source)
        {
            var tokens = new List<IoTToken>();
            var errors = new List<string>();
            int i = 0;
            Action<IoTTokenType, string, int> add = (type, text, start) => tokens.Add(new IoTToken(type, text, start));
            while (i < source.Length)
            {
                char c = source[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '#') { while (i < source.Length && source[i] != '\n') i++; continue; }
                if (c == '"' || c == '\'')
                {
                    char quote = c; int start = i++; var value = new StringBuilder(); bool closed = false;
                    while (i < source.Length)
                    {
                        char ch = source[i++];
                        if (ch == '\\' && i < source.Length)
                        {
                            char next = source[i++];
                            value.Append(next == 'n' ? '\n' : next == 'r' ? '\r' : next == 't' ? '\t' : next);
                        }
                        else if (ch == quote) { closed = true; break; }
                        else value.Append(ch);
                    }
                    if (!closed) errors.Add("Незакрытая строка в позиции " + start);
                    else add(IoTTokenType.String, value.ToString(), start);
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
                {
                    int start = i; bool dot = false;
                    while (i < source.Length)
                    {
                        char ch = source[i];
                        if (char.IsDigit(ch)) i++;
                        else if (ch == '.' && !dot) { dot = true; i++; }
                        else break;
                    }
                    add(IoTTokenType.Number, source.Substring(start, i - start), start); continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i++;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_' || source[i] == '.')) i++;
                    string word = source.Substring(start, i - start);
                    string lower = word.ToLowerInvariant();
                    add(lower == "if" ? IoTTokenType.If : lower == "then" ? IoTTokenType.Then :
                        lower == "else" ? IoTTokenType.Else : IoTTokenType.Identifier, word, start);
                    continue;
                }
                int p = i;
                if (i + 1 < source.Length)
                {
                    string pair = source.Substring(i, 2);
                    IoTTokenType pairType;
                    if (pair == ":=") pairType = IoTTokenType.SilentAssign;
                    else if (pair == "==") pairType = IoTTokenType.Equal;
                    else if (pair == "!=") pairType = IoTTokenType.NotEqual;
                    else if (pair == "<=") pairType = IoTTokenType.LessEqual;
                    else if (pair == ">=") pairType = IoTTokenType.GreaterEqual;
                    else pairType = (IoTTokenType)(-1);
                    if ((int)pairType >= 0) { add(pairType, pair, p); i += 2; continue; }
                }
                IoTTokenType type;
                switch (c)
                {
                    case '=': type = IoTTokenType.Assign; break; case '<': type = IoTTokenType.Less; break;
                    case '>': type = IoTTokenType.Greater; break; case '+': type = IoTTokenType.Plus; break;
                    case '-': type = IoTTokenType.Minus; break; case '*': type = IoTTokenType.Multiply; break;
                    case '/': type = IoTTokenType.Divide; break; case '&': type = IoTTokenType.BitAnd; break;
                    case '|': type = IoTTokenType.BitOr; break; case '!': type = IoTTokenType.Bang; break;
                    case '(': type = IoTTokenType.LeftParen; break; case ')': type = IoTTokenType.RightParen; break;
                    case '{': type = IoTTokenType.LeftBrace; break; case '}': type = IoTTokenType.RightBrace; break;
                    case ',': type = IoTTokenType.Comma; break; case ';': type = IoTTokenType.Semicolon; break;
                    default: errors.Add("Неизвестный символ '" + c + "' в позиции " + i); i++; continue;
                }
                add(type, c.ToString(), p); i++;
            }
            tokens.Add(new IoTToken(IoTTokenType.End, "", source.Length));
            return Tuple.Create(tokens, errors);
        }
    }

    public static class IoTScenarioParser
    {
        public static IoTScenarioParseResult Parse(string source)
        {
            source = source ?? "";
            var marker = Regex.Match(source, @"(?m)^\s*scenario=>");
            if (marker.Success) source = source.Substring(marker.Index + marker.Length);
            var lexed = IoTScenarioLexer.Tokenize(source);
            return new Parser(lexed.Item1, lexed.Item2).Run();
        }

        private sealed class Parser
        {
            private readonly IList<IoTToken> _tokens;
            private readonly List<string> _errors;
            private int _index;
            public Parser(IList<IoTToken> tokens, IList<string> errors) { _tokens = tokens; _errors = new List<string>(errors); }
            public IoTScenarioParseResult Run()
            {
                var statements = new List<IoTStatement>();
                while (!Check(IoTTokenType.End))
                {
                    if (Match(IoTTokenType.Semicolon)) continue;
                    int before = _index; var statement = ParseStatement();
                    if (statement != null) statements.Add(statement);
                    if (before == _index) Advance();
                }
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var statement in statements) Collect(statement, ids);
                return new IoTScenarioParseResult { Statements = statements, Errors = _errors, ReferencedIdentifiers = ids };
            }
            private IoTStatement ParseStatement()
            {
                if (Match(IoTTokenType.If)) return ParseIf(Previous());
                if (Check(IoTTokenType.LeftBrace)) return ParseBlock();
                return ParseAssignmentOrExpression();
            }
            private IoTStatement ParseIf(IoTToken start)
            {
                var condition = ParseExpression();
                if (!Match(IoTTokenType.Then)) { Error(Current(), "Ожидалось THEN после условия IF"); Synchronize(); return null; }
                var thenBranch = ParseStatement();
                if (thenBranch == null) { Error(start, "Не удалось разобрать ветвь THEN"); return null; }
                var elseBranch = Match(IoTTokenType.Else) ? ParseStatement() : null;
                return new IoTStatement.If(condition, thenBranch, elseBranch);
            }
            private IoTStatement ParseBlock()
            {
                Advance(); var statements = new List<IoTStatement>();
                while (!Check(IoTTokenType.RightBrace) && !Check(IoTTokenType.End))
                {
                    if (Match(IoTTokenType.Semicolon)) continue;
                    int before = _index; var item = ParseStatement();
                    if (item != null) statements.Add(item);
                    if (before == _index) Advance();
                }
                Consume(IoTTokenType.RightBrace, "Ожидалась '}'"); Match(IoTTokenType.Semicolon);
                return new IoTStatement.Block(statements);
            }
            private IoTStatement ParseAssignmentOrExpression()
            {
                var left = ParseExpression(); string op = null;
                if (Match(IoTTokenType.Assign)) op = "=";
                else if (Match(IoTTokenType.SilentAssign)) op = ":=";
                if (op != null)
                {
                    var variable = left as IoTExpr.Variable;
                    if (variable == null) { Error(Previous(), "Левая часть присваивания должна быть идентификатором"); return null; }
                    var rhs = ParseExpression(); Match(IoTTokenType.Semicolon);
                    return new IoTStatement.Assignment(variable.Name, op, rhs);
                }
                Match(IoTTokenType.Semicolon);
                return new IoTStatement.ExpressionStatement(left);
            }
            private IoTExpr ParseExpression() { return ParseBinary(0); }
            private IoTExpr ParseBinary(int minimum)
            {
                var left = ParseUnary();
                while (true)
                {
                    int precedence = Precedence(Current().Type);
                    if (precedence < minimum) break;
                    var op = Advance().Text;
                    left = new IoTExpr.Binary(op, left, ParseBinary(precedence + 1));
                }
                return left;
            }
            private IoTExpr ParseUnary()
            {
                if (Match(IoTTokenType.Minus)) return new IoTExpr.Unary("-", ParseUnary());
                if (Match(IoTTokenType.Plus)) return new IoTExpr.Unary("+", ParseUnary());
                if (Match(IoTTokenType.Bang)) return new IoTExpr.Unary("!", ParseUnary());
                return ParsePrimary();
            }
            private IoTExpr ParsePrimary()
            {
                var token = Current();
                if (Match(IoTTokenType.Number))
                {
                    double value; double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                    return new IoTExpr.NumberLiteral(value);
                }
                if (Match(IoTTokenType.String)) return new IoTExpr.StringLiteral(token.Text);
                if (Match(IoTTokenType.Identifier))
                {
                    if (Match(IoTTokenType.LeftParen))
                    {
                        var args = new List<IoTExpr>();
                        if (!Check(IoTTokenType.RightParen))
                        {
                            do { args.Add(ParseExpression()); } while (Match(IoTTokenType.Comma));
                        }
                        Consume(IoTTokenType.RightParen, "Ожидалась ')' после вызова");
                        return new IoTExpr.Call(token.Text, args);
                    }
                    return new IoTExpr.Variable(token.Text);
                }
                if (Match(IoTTokenType.LeftParen))
                {
                    var expression = ParseExpression(); Consume(IoTTokenType.RightParen, "Ожидалась ')'");
                    return expression;
                }
                Error(token, "Ожидалось выражение");
                if (!Check(IoTTokenType.End)) Advance();
                return new IoTExpr.NumberLiteral(0);
            }
            private static int Precedence(IoTTokenType type)
            {
                switch (type)
                {
                    case IoTTokenType.BitOr: return 1; case IoTTokenType.BitAnd: return 2;
                    case IoTTokenType.Equal: case IoTTokenType.NotEqual: return 3;
                    case IoTTokenType.Less: case IoTTokenType.LessEqual: case IoTTokenType.Greater: case IoTTokenType.GreaterEqual: return 4;
                    case IoTTokenType.Plus: case IoTTokenType.Minus: return 5;
                    case IoTTokenType.Multiply: case IoTTokenType.Divide: return 6;
                    default: return -1;
                }
            }
            private void Synchronize()
            {
                while (!Check(IoTTokenType.End))
                {
                    if (Match(IoTTokenType.Semicolon)) return;
                    if (Check(IoTTokenType.If) || Check(IoTTokenType.RightBrace) || Check(IoTTokenType.Else)) return;
                    Advance();
                }
            }
            private IoTToken Consume(IoTTokenType type, string message) { if (Check(type)) return Advance(); Error(Current(), message); return Current(); }
            private bool Check(IoTTokenType type) { return Current().Type == type; }
            private bool Match(IoTTokenType type) { if (!Check(type)) return false; Advance(); return true; }
            private IoTToken Advance() { var token = Current(); if (_index < _tokens.Count - 1) _index++; return token; }
            private IoTToken Current() { return _tokens[_index]; }
            private IoTToken Previous() { return _tokens[Math.Max(0, _index - 1)]; }
            private void Error(IoTToken token, string message) { _errors.Add(message + " (позиция " + token.Position + ")"); }
            private static void CollectExpr(IoTExpr e, ISet<string> ids)
            {
                var v = e as IoTExpr.Variable; if (v != null) { ids.Add(v.Name); return; }
                var u = e as IoTExpr.Unary; if (u != null) { CollectExpr(u.Expression, ids); return; }
                var b = e as IoTExpr.Binary; if (b != null) { CollectExpr(b.Left, ids); CollectExpr(b.Right, ids); return; }
                var call = e as IoTExpr.Call; if (call != null) foreach (var arg in call.Arguments) CollectExpr(arg, ids);
            }
            private static void Collect(IoTStatement s, ISet<string> ids)
            {
                var a = s as IoTStatement.Assignment; if (a != null) { ids.Add(a.Target); CollectExpr(a.Expression, ids); return; }
                var e = s as IoTStatement.ExpressionStatement; if (e != null) { CollectExpr(e.Expression, ids); return; }
                var f = s as IoTStatement.If; if (f != null) { CollectExpr(f.Condition, ids); Collect(f.ThenBranch, ids); if (f.ElseBranch != null) Collect(f.ElseBranch, ids); return; }
                var block = s as IoTStatement.Block; if (block != null) foreach (var child in block.Statements) Collect(child, ids);
            }
        }
    }

    public static class IoTScenarioSemanticAnalyzer
    {
        public static DeviceScenarioModel Analyze(IoTScenarioParseResult parsed)
        {
            var rules = new List<DeviceScenarioRule>(); int next = 0;
            foreach (var statement in parsed.Statements) Walk(statement, null, rules, ref next);
            return new DeviceScenarioModel { Rules = rules, Identifiers = parsed.ReferencedIdentifiers,
                ParserErrors = parsed.Errors, Statements = parsed.Statements };
        }
        private static IoTExpr Combine(IoTExpr left, IoTExpr right) { return left == null ? right : new IoTExpr.Binary("&", left, right); }
        private static void Emit(IoTExpr condition, IList<IoTStatement.Assignment> assignments, IList<DeviceScenarioRule> rules, ref int next)
        {
            if (assignments.Count == 0) return;
            var actions = assignments.Select(a => new DeviceScenarioAction { TargetId = a.Target, Operator = a.Operator,
                Expression = a.Expression, Rendered = a.Target + " " + a.Operator + " " + a.Expression.Render() }).ToList();
            rules.Add(new DeviceScenarioRule { Index = next++, Condition = new DeviceScenarioCondition {
                Expression = condition, Rendered = condition.Render(), Identifiers = condition.Identifiers() }, Actions = actions });
        }
        private static void Walk(IoTStatement statement, IoTExpr parent, IList<DeviceScenarioRule> rules, ref int next)
        {
            var assignment = statement as IoTStatement.Assignment;
            if (assignment != null) { Emit(parent ?? new IoTExpr.NumberLiteral(1), new[] { assignment }, rules, ref next); return; }
            var expression = statement as IoTStatement.ExpressionStatement;
            if (expression != null) return;
            var block = statement as IoTStatement.Block;
            if (block != null)
            {
                var assignments = block.Statements.OfType<IoTStatement.Assignment>().ToList();
                Emit(parent ?? new IoTExpr.NumberLiteral(1), assignments, rules, ref next);
                foreach (var child in block.Statements.Where(x => !(x is IoTStatement.Assignment))) Walk(child, parent, rules, ref next);
                return;
            }
            var conditional = statement as IoTStatement.If;
            if (conditional != null)
            {
                Walk(conditional.ThenBranch, Combine(parent, conditional.Condition), rules, ref next);
                if (conditional.ElseBranch != null) Walk(conditional.ElseBranch,
                    Combine(parent, new IoTExpr.Unary("!", conditional.Condition)), rules, ref next);
            }
        }
    }

    public static class IoTScenarioExpressionExtensions
    {
        public static ISet<string> Identifiers(this IoTExpr expression)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase); Collect(expression, result); return result;
        }
        private static void Collect(IoTExpr e, ISet<string> ids)
        {
            var v = e as IoTExpr.Variable; if (v != null) { ids.Add(v.Name); return; }
            var u = e as IoTExpr.Unary; if (u != null) { Collect(u.Expression, ids); return; }
            var b = e as IoTExpr.Binary; if (b != null) { Collect(b.Left, ids); Collect(b.Right, ids); return; }
            var call = e as IoTExpr.Call; if (call != null) foreach (var arg in call.Arguments) Collect(arg, ids);
        }
        public static string Render(this IoTExpr expression)
        {
            var n = expression as IoTExpr.NumberLiteral; if (n != null) return n.Value.ToString("0.################", CultureInfo.InvariantCulture);
            var s = expression as IoTExpr.StringLiteral; if (s != null) return "\"" + s.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var v = expression as IoTExpr.Variable; if (v != null) return v.Name;
            var u = expression as IoTExpr.Unary; if (u != null) return u.Operator + u.Expression.Render();
            var b = expression as IoTExpr.Binary; if (b != null) return "(" + b.Left.Render() + " " + b.Operator + " " + b.Right.Render() + ")";
            var c = expression as IoTExpr.Call; if (c != null) return c.Name + "(" + string.Join(", ", c.Arguments.Select(x => x.Render()).ToArray()) + ")";
            return "";
        }
        public static string Render(this IoTExpr expression, IDictionary<string, string> labels)
        {
            var v = expression as IoTExpr.Variable;
            if (v != null) { string label; return labels != null && labels.TryGetValue(v.Name, out label) && !string.IsNullOrWhiteSpace(label) && label != v.Name ? label + " [" + v.Name + "]" : v.Name; }
            var n = expression as IoTExpr.NumberLiteral; if (n != null) return n.Value.ToString("0.################", CultureInfo.InvariantCulture);
            var s = expression as IoTExpr.StringLiteral; if (s != null) return "\"" + s.Value.Replace("\"", "\\\"") + "\"";
            var u = expression as IoTExpr.Unary; if (u != null) return u.Operator + u.Expression.Render(labels);
            var b = expression as IoTExpr.Binary; if (b != null) return "(" + b.Left.Render(labels) + " " + b.Operator + " " + b.Right.Render(labels) + ")";
            var c = expression as IoTExpr.Call; if (c != null) return c.Name + "(" + string.Join(", ", c.Arguments.Select(x => x.Render(labels)).ToArray()) + ")";
            return "";
        }
    }

    public abstract class IoTValue
    {
        public sealed class Number : IoTValue { public double Value; public Number(double value) { Value = value; } }
        public sealed class Text : IoTValue { public string Value; public Text(string value) { Value = value; } }
        public sealed class BooleanValue : IoTValue { public bool Value; public BooleanValue(bool value) { Value = value; } }
        public sealed class Unknown : IoTValue { private Unknown() { } public static readonly Unknown Instance = new Unknown(); }
    }
    public sealed class IoTScenarioEvaluationContext
    {
        public IDictionary<string, string> Variables { get; private set; }
        public IDictionary<string, Func<IList<IoTValue>, IoTValue>> Functions { get; private set; }
        public IoTScenarioEvaluationContext(IDictionary<string, string> variables, IDictionary<string, Func<IList<IoTValue>, IoTValue>> functions = null)
        {
            Variables = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Functions = functions ?? new Dictionary<string, Func<IList<IoTValue>, IoTValue>>(StringComparer.OrdinalIgnoreCase);
        }
    }
    public static class IoTScenarioEvaluator
    {
        public static IoTValue Evaluate(IoTExpr expression, IoTScenarioEvaluationContext context)
        {
            var n = expression as IoTExpr.NumberLiteral; if (n != null) return new IoTValue.Number(n.Value);
            var s = expression as IoTExpr.StringLiteral; if (s != null) return new IoTValue.Text(s.Value);
            var v = expression as IoTExpr.Variable; if (v != null) return Variable(v.Name, context);
            var u = expression as IoTExpr.Unary;
            if (u != null)
            {
                var value = Evaluate(u.Expression, context); double number;
                if (u.Operator == "+" || u.Operator == "-")
                    return TryNumber(value, out number) ? (IoTValue)new IoTValue.Number(u.Operator == "-" ? -number : number) : IoTValue.Unknown.Instance;
                if (u.Operator == "!") return value is IoTValue.Unknown ? (IoTValue)IoTValue.Unknown.Instance : new IoTValue.BooleanValue(!Truthy(value));
                return IoTValue.Unknown.Instance;
            }
            var b = expression as IoTExpr.Binary; if (b != null) return EvaluateBinary(b, context);
            var call = expression as IoTExpr.Call;
            if (call != null)
            {
                Func<IList<IoTValue>, IoTValue> function;
                var args = call.Arguments.Select(x => Evaluate(x, context)).ToList();
                if (context.Functions.TryGetValue(call.Name, out function)) return function(args) ?? IoTValue.Unknown.Instance;
                return Builtin(call.Name);
            }
            return IoTValue.Unknown.Instance;
        }
        private static IoTValue Variable(string name, IoTScenarioEvaluationContext context)
        {
            string raw;
            if (!context.Variables.TryGetValue(name, out raw))
                return name.Equals("true", StringComparison.OrdinalIgnoreCase) ? (IoTValue)new IoTValue.BooleanValue(true) :
                    name.Equals("false", StringComparison.OrdinalIgnoreCase) ? (IoTValue)new IoTValue.BooleanValue(false) : IoTValue.Unknown.Instance;
            double number;
            return double.TryParse((raw ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? (IoTValue)new IoTValue.Number(number) : new IoTValue.Text(raw ?? "");
        }
        private static IoTValue EvaluateBinary(IoTExpr.Binary e, IoTScenarioEvaluationContext context)
        {
            var left = Evaluate(e.Left, context); var right = Evaluate(e.Right, context); double a, b;
            if (e.Operator == "+" || e.Operator == "-" || e.Operator == "*" || e.Operator == "/")
            {
                if (!TryNumber(left, out a) || !TryNumber(right, out b) || (e.Operator == "/" && Math.Abs(b) < 0.0000001)) return IoTValue.Unknown.Instance;
                return new IoTValue.Number(e.Operator == "+" ? a + b : e.Operator == "-" ? a - b : e.Operator == "*" ? a * b : a / b);
            }
            if (e.Operator == "&" || e.Operator == "|")
            {
                var ln = left as IoTValue.Number; var rn = right as IoTValue.Number;
                if (ln != null && rn != null)
                {
                    long x = (long)ln.Value, y = (long)rn.Value;
                    return new IoTValue.Number(e.Operator == "&" ? (double)(x & y) : (double)(x | y));
                }
                if (left is IoTValue.Unknown || right is IoTValue.Unknown) return IoTValue.Unknown.Instance;
                return new IoTValue.BooleanValue(e.Operator == "&" ? Truthy(left) && Truthy(right) : Truthy(left) || Truthy(right));
            }
            int comparison;
            if (!Compare(left, right, out comparison)) return IoTValue.Unknown.Instance;
            switch (e.Operator)
            {
                case "==": return new IoTValue.BooleanValue(comparison == 0);
                case "!=": return new IoTValue.BooleanValue(comparison != 0);
                case "<": return new IoTValue.BooleanValue(comparison < 0);
                case "<=": return new IoTValue.BooleanValue(comparison <= 0);
                case ">": return new IoTValue.BooleanValue(comparison > 0);
                case ">=": return new IoTValue.BooleanValue(comparison >= 0);
                default: return IoTValue.Unknown.Instance;
            }
        }
        private static IoTValue Builtin(string name)
        {
            var now = DateTime.Now;
            switch (name.ToLowerInvariant())
            {
                case "gethours": return new IoTValue.Number(now.Hour);
                case "getminutes": return new IoTValue.Number(now.Minute);
                case "getseconds": return new IoTValue.Number(now.Second);
                case "getmonth": return new IoTValue.Number(now.Month);
                case "getday": return new IoTValue.Number(now.Day);
                case "gethhmm": return new IoTValue.Number(now.Hour * 100 + now.Minute);
                case "gethhmmss": return new IoTValue.Number(now.Hour * 10000 + now.Minute * 100 + now.Second);
                default: return IoTValue.Unknown.Instance;
            }
        }
        private static bool TryNumber(IoTValue value, out double number) { var n = value as IoTValue.Number; number = n == null ? 0 : n.Value; return n != null; }
        private static bool Compare(IoTValue left, IoTValue right, out int comparison)
        {
            comparison = 0; if (left is IoTValue.Unknown || right is IoTValue.Unknown) return false;
            double a, b;
            if (TryNumber(left, out a) && TryNumber(right, out b)) { comparison = a.CompareTo(b); return true; }
            string x = AsText(left), y = AsText(right); if (x == null || y == null) return false;
            comparison = string.Compare(x, y, StringComparison.Ordinal); return true;
        }
        private static string AsText(IoTValue value)
        {
            var s = value as IoTValue.Text; if (s != null) return s.Value;
            var n = value as IoTValue.Number; if (n != null) return n.Value.ToString(CultureInfo.InvariantCulture);
            var b = value as IoTValue.BooleanValue; if (b != null) return b.Value.ToString();
            return null;
        }
        public static bool IsTruthy(IoTValue value) { return Truthy(value); }
        private static bool Truthy(IoTValue value)
        {
            var b = value as IoTValue.BooleanValue; if (b != null) return b.Value;
            var n = value as IoTValue.Number; if (n != null) return Math.Abs(n.Value) > 0.000001;
            var s = value as IoTValue.Text; return s != null && !string.IsNullOrWhiteSpace(s.Value) && s.Value != "0" && !s.Value.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }
}