using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    internal abstract class ScenarioExpression { }

    internal sealed class ScenarioNumberExpression : ScenarioExpression
    {
        public double Value;
        public ScenarioNumberExpression(double value) { Value = value; }
    }

    internal sealed class ScenarioTextExpression : ScenarioExpression
    {
        public string Value;
        public ScenarioTextExpression(string value) { Value = value ?? string.Empty; }
    }

    internal sealed class ScenarioVariableExpression : ScenarioExpression
    {
        public string Name;
        public ScenarioVariableExpression(string name) { Name = name ?? string.Empty; }
    }

    internal sealed class ScenarioUnaryExpression : ScenarioExpression
    {
        public string Operator;
        public ScenarioExpression Expression;
        public ScenarioUnaryExpression(string op, ScenarioExpression expression)
        {
            Operator = op;
            Expression = expression;
        }
    }

    internal sealed class ScenarioBinaryExpression : ScenarioExpression
    {
        public string Operator;
        public ScenarioExpression Left;
        public ScenarioExpression Right;
        public ScenarioBinaryExpression(string op, ScenarioExpression left, ScenarioExpression right)
        {
            Operator = op;
            Left = left;
            Right = right;
        }
    }

    internal sealed class ScenarioCallExpression : ScenarioExpression
    {
        public string Name;
        public IList<ScenarioExpression> Arguments;
        public ScenarioCallExpression(string name, IList<ScenarioExpression> arguments)
        {
            Name = name ?? string.Empty;
            Arguments = arguments ?? new List<ScenarioExpression>();
        }
    }

    internal abstract class ScenarioStatement { }

    internal sealed class ScenarioIfStatement : ScenarioStatement
    {
        public ScenarioExpression Condition;
        public ScenarioStatement ThenBranch;
        public ScenarioStatement ElseBranch;
    }

    internal sealed class ScenarioAssignmentStatement : ScenarioStatement
    {
        public string Target;
        public string Operator;
        public ScenarioExpression Expression;
    }

    internal sealed class ScenarioExpressionStatement : ScenarioStatement
    {
        public ScenarioExpression Expression;
    }

    internal sealed class ScenarioBlockStatement : ScenarioStatement
    {
        public IList<ScenarioStatement> Statements = new List<ScenarioStatement>();
    }

    internal sealed class ScenarioLanguageAction
    {
        public string TargetId;
        public string Operator;
        public ScenarioExpression Expression;
        public string Rendered;
    }

    internal sealed class ScenarioLanguageRule
    {
        public int Index;
        public ScenarioExpression Condition;
        public string RenderedCondition;
        public IList<string> Identifiers = new List<string>();
        public IList<ScenarioLanguageAction> Actions = new List<ScenarioLanguageAction>();
    }

    internal sealed class ScenarioLanguageModel
    {
        public IList<ScenarioLanguageRule> Rules = new List<ScenarioLanguageRule>();
        public IList<string> Identifiers = new List<string>();
        public IList<string> Errors = new List<string>();
        public bool IsValid { get { return Errors.Count == 0 && Rules.Count > 0; } }
    }

    internal enum ScenarioTokenType
    {
        Identifier, Number, Text, If, Then, Else,
        LeftParen, RightParen, LeftBrace, RightBrace, Comma, Semicolon,
        Assign, SilentAssign, Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual,
        Plus, Minus, Multiply, Divide, And, Or, Bang, End
    }

    internal sealed class ScenarioToken
    {
        public ScenarioTokenType Type;
        public string Text;
        public int Position;
        public ScenarioToken(ScenarioTokenType type, string text, int position)
        {
            Type = type;
            Text = text ?? string.Empty;
            Position = position;
        }
    }

    internal sealed class ScenarioLanguageParser
    {
        private readonly IList<ScenarioToken> _tokens;
        private readonly IList<string> _errors;
        private int _index;

        private ScenarioLanguageParser(IList<ScenarioToken> tokens, IList<string> errors)
        {
            _tokens = tokens;
            _errors = errors;
        }

        public static ScenarioLanguageModel Compile(string source)
        {
            var result = new ScenarioLanguageModel();
            var normalized = Regex.Replace(source ?? string.Empty, @"(?im)^\s*scenario\s*=>\s*", string.Empty);
            IList<string> lexerErrors;
            var tokens = Tokenize(normalized, out lexerErrors);
            foreach (var error in lexerErrors)
                result.Errors.Add(error);

            var parser = new ScenarioLanguageParser(tokens, result.Errors);
            var statements = parser.ParseProgram();
            var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var statement in statements)
                CollectIdentifiers(statement, identifiers);
            result.Identifiers = identifiers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            var semantic = new ScenarioLanguageParser(tokens, new List<string>());
            semantic.BuildRules(statements, result);
            return result;
        }

        private static IList<ScenarioToken> Tokenize(string source, out IList<string> errors)
        {
            var tokens = new List<ScenarioToken>();
            var problems = new List<string>();
            var i = 0;

            while (i < source.Length)
            {
                var c = source[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '#')
                {
                    while (i < source.Length && source[i] != '\n') i++;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    var quote = c;
                    var start = i++;
                    var value = new StringBuilder();
                    var closed = false;
                    while (i < source.Length)
                    {
                        var ch = source[i++];
                        if (ch == '\\' && i < source.Length)
                        {
                            var next = source[i++];
                            switch (next)
                            {
                                case 'n': value.Append('\n'); break;
                                case 'r': value.Append('\r'); break;
                                case 't': value.Append('\t'); break;
                                default: value.Append(next); break;
                            }
                        }
                        else if (ch == quote)
                        {
                            closed = true;
                            break;
                        }
                        else
                        {
                            value.Append(ch);
                        }
                    }

                    if (closed)
                        tokens.Add(new ScenarioToken(ScenarioTokenType.Text, value.ToString(), start));
                    else
                        problems.Add("Незакрытая строка в позиции " + start.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
                {
                    var start = i;
                    var dot = false;
                    while (i < source.Length)
                    {
                        var ch = source[i];
                        if (char.IsDigit(ch)) i++;
                        else if (ch == '.' && !dot) { dot = true; i++; }
                        else break;
                    }
                    tokens.Add(new ScenarioToken(ScenarioTokenType.Number, source.Substring(start, i - start), start));
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    var start = i++;
                    while (i < source.Length)
                    {
                        var ch = source[i];
                        if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.') i++;
                        else break;
                    }

                    var word = source.Substring(start, i - start);
                    var lower = word.ToLowerInvariant();
                    var type = lower == "if" ? ScenarioTokenType.If :
                               lower == "then" ? ScenarioTokenType.Then :
                               lower == "else" ? ScenarioTokenType.Else :
                               ScenarioTokenType.Identifier;
                    tokens.Add(new ScenarioToken(type, word, start));
                    continue;
                }

                var position = i;
                if (i + 1 < source.Length)
                {
                    var pair = source.Substring(i, 2);
                    ScenarioTokenType pairType;
                    if (pair == ":=") pairType = ScenarioTokenType.SilentAssign;
                    else if (pair == "==") pairType = ScenarioTokenType.Equal;
                    else if (pair == "!=") pairType = ScenarioTokenType.NotEqual;
                    else if (pair == "<=") pairType = ScenarioTokenType.LessOrEqual;
                    else if (pair == ">=") pairType = ScenarioTokenType.GreaterOrEqual;
                    else pairType = (ScenarioTokenType)(-1);

                    if ((int)pairType >= 0)
                    {
                        tokens.Add(new ScenarioToken(pairType, pair, position));
                        i += 2;
                        continue;
                    }
                }

                ScenarioTokenType oneType;
                switch (c)
                {
                    case '=': oneType = ScenarioTokenType.Assign; break;
                    case '<': oneType = ScenarioTokenType.Less; break;
                    case '>': oneType = ScenarioTokenType.Greater; break;
                    case '+': oneType = ScenarioTokenType.Plus; break;
                    case '-': oneType = ScenarioTokenType.Minus; break;
                    case '*': oneType = ScenarioTokenType.Multiply; break;
                    case '/': oneType = ScenarioTokenType.Divide; break;
                    case '&': oneType = ScenarioTokenType.And; break;
                    case '|': oneType = ScenarioTokenType.Or; break;
                    case '!': oneType = ScenarioTokenType.Bang; break;
                    case '(': oneType = ScenarioTokenType.LeftParen; break;
                    case ')': oneType = ScenarioTokenType.RightParen; break;
                    case '{': oneType = ScenarioTokenType.LeftBrace; break;
                    case '}': oneType = ScenarioTokenType.RightBrace; break;
                    case ',': oneType = ScenarioTokenType.Comma; break;
                    case ';': oneType = ScenarioTokenType.Semicolon; break;
                    default:
                        problems.Add("Неизвестный символ '" + c + "' в позиции " + i.ToString(CultureInfo.InvariantCulture));
                        i++;
                        continue;
                }
                tokens.Add(new ScenarioToken(oneType, c.ToString(), position));
                i++;
            }

            tokens.Add(new ScenarioToken(ScenarioTokenType.End, string.Empty, source.Length));
            errors = problems;
            return tokens;
        }

        private IList<ScenarioStatement> ParseProgram()
        {
            var statements = new List<ScenarioStatement>();
            while (!Check(ScenarioTokenType.End))
            {
                if (Match(ScenarioTokenType.Semicolon)) continue;
                var before = _index;
                var statement = ParseStatement();
                if (statement != null) statements.Add(statement);
                if (_index == before) Advance();
            }
            return statements;
        }

        private ScenarioStatement ParseStatement()
        {
            if (Match(ScenarioTokenType.If))
                return ParseIf(Previous());
            if (Check(ScenarioTokenType.LeftBrace))
                return ParseBlock();
            return ParseAssignmentOrExpression();
        }

        private ScenarioStatement ParseIf(ScenarioToken start)
        {
            var condition = ParseExpression();
            if (!Match(ScenarioTokenType.Then))
            {
                AddError(Current(), "Ожидалось THEN после условия IF");
                SynchronizeStatement();
                return null;
            }

            var thenBranch = ParseStatement();
            if (thenBranch == null)
            {
                AddError(start, "Не удалось разобрать ветвь THEN");
                return null;
            }

            ScenarioStatement elseBranch = null;
            if (Match(ScenarioTokenType.Else))
                elseBranch = ParseStatement();

            return new ScenarioIfStatement
            {
                Condition = condition,
                ThenBranch = thenBranch,
                ElseBranch = elseBranch
            };
        }

        private ScenarioStatement ParseBlock()
        {
            Consume(ScenarioTokenType.LeftBrace, "Ожидалась '{'");
            var block = new ScenarioBlockStatement();
            while (!Check(ScenarioTokenType.RightBrace) && !Check(ScenarioTokenType.End))
            {
                if (Match(ScenarioTokenType.Semicolon)) continue;
                var before = _index;
                var statement = ParseStatement();
                if (statement != null) block.Statements.Add(statement);
                if (_index == before) Advance();
            }
            Consume(ScenarioTokenType.RightBrace, "Ожидалась '}'");
            Match(ScenarioTokenType.Semicolon);
            return block;
        }

        private ScenarioStatement ParseAssignmentOrExpression()
        {
            var left = ParseExpression();
            string op = null;
            if (Match(ScenarioTokenType.Assign)) op = "=";
            else if (Match(ScenarioTokenType.SilentAssign)) op = ":=";

            if (op != null)
            {
                var variable = left as ScenarioVariableExpression;
                if (variable == null)
                {
                    AddError(Previous(), "Левая часть присваивания должна быть идентификатором");
                    return null;
                }

                var right = ParseExpression();
                Match(ScenarioTokenType.Semicolon);
                return new ScenarioAssignmentStatement
                {
                    Target = variable.Name,
                    Operator = op,
                    Expression = right
                };
            }

            Match(ScenarioTokenType.Semicolon);
            return new ScenarioExpressionStatement { Expression = left };
        }

        private ScenarioExpression ParseExpression()
        {
            return ParseBinary(0);
        }

        private ScenarioExpression ParseBinary(int minimumPrecedence)
        {
            var left = ParseUnary();
            while (true)
            {
                var token = Current();
                var precedence = Precedence(token.Type);
                if (precedence < minimumPrecedence) break;
                Advance();
                var right = ParseBinary(precedence + 1);
                left = new ScenarioBinaryExpression(token.Text, left, right);
            }
            return left;
        }

        private ScenarioExpression ParseUnary()
        {
            if (Match(ScenarioTokenType.Minus))
                return new ScenarioUnaryExpression("-", ParseUnary());
            if (Match(ScenarioTokenType.Plus))
                return new ScenarioUnaryExpression("+", ParseUnary());
            if (Match(ScenarioTokenType.Bang))
                return new ScenarioUnaryExpression("!", ParseUnary());
            return ParsePrimary();
        }

        private ScenarioExpression ParsePrimary()
        {
            var token = Current();
            if (Match(ScenarioTokenType.Number))
            {
                double number;
                if (!double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    number = 0.0;
                return new ScenarioNumberExpression(number);
            }

            if (Match(ScenarioTokenType.Text))
                return new ScenarioTextExpression(token.Text);

            if (Match(ScenarioTokenType.Identifier))
            {
                if (Match(ScenarioTokenType.LeftParen))
                {
                    var arguments = new List<ScenarioExpression>();
                    if (!Check(ScenarioTokenType.RightParen))
                    {
                        do { arguments.Add(ParseExpression()); }
                        while (Match(ScenarioTokenType.Comma));
                    }
                    Consume(ScenarioTokenType.RightParen, "Ожидалась ')' после вызова");
                    return new ScenarioCallExpression(token.Text, arguments);
                }
                return new ScenarioVariableExpression(token.Text);
            }

            if (Match(ScenarioTokenType.LeftParen))
            {
                var expression = ParseExpression();
                Consume(ScenarioTokenType.RightParen, "Ожидалась ')'");
                return expression;
            }

            AddError(token, "Ожидалось выражение");
            if (!Check(ScenarioTokenType.End)) Advance();
            return new ScenarioNumberExpression(0.0);
        }

        private static int Precedence(ScenarioTokenType type)
        {
            switch (type)
            {
                case ScenarioTokenType.Or: return 1;
                case ScenarioTokenType.And: return 2;
                case ScenarioTokenType.Equal:
                case ScenarioTokenType.NotEqual: return 3;
                case ScenarioTokenType.Less:
                case ScenarioTokenType.LessOrEqual:
                case ScenarioTokenType.Greater:
                case ScenarioTokenType.GreaterOrEqual: return 4;
                case ScenarioTokenType.Plus:
                case ScenarioTokenType.Minus: return 5;
                case ScenarioTokenType.Multiply:
                case ScenarioTokenType.Divide: return 6;
                default: return -1;
            }
        }

        private void SynchronizeStatement()
        {
            while (!Check(ScenarioTokenType.End))
            {
                if (Match(ScenarioTokenType.Semicolon)) return;
                if (Check(ScenarioTokenType.If) || Check(ScenarioTokenType.RightBrace) || Check(ScenarioTokenType.Else))
                    return;
                Advance();
            }
        }

        private ScenarioToken Consume(ScenarioTokenType type, string message)
        {
            if (Check(type)) return Advance();
            AddError(Current(), message);
            return Current();
        }

        private bool Check(ScenarioTokenType type) { return Current().Type == type; }

        private bool Match(ScenarioTokenType type)
        {
            if (!Check(type)) return false;
            Advance();
            return true;
        }

        private ScenarioToken Advance()
        {
            var token = Current();
            if (_index < _tokens.Count - 1) _index++;
            return token;
        }

        private ScenarioToken Current() { return _tokens[Math.Min(_index, _tokens.Count - 1)]; }
        private ScenarioToken Previous() { return _tokens[Math.Max(0, _index - 1)]; }

        private void AddError(ScenarioToken token, string message)
        {
            _errors.Add(message + " (позиция " + token.Position.ToString(CultureInfo.InvariantCulture) + ")");
        }

        private void BuildRules(IList<ScenarioStatement> statements, ScenarioLanguageModel model)
        {
            var index = 0;
            foreach (var statement in statements)
                Walk(statement, null, model, ref index);
        }

        private static ScenarioExpression Combine(ScenarioExpression left, ScenarioExpression right)
        {
            return left == null ? right : new ScenarioBinaryExpression("&", left, right);
        }

        private static ScenarioExpression Negate(ScenarioExpression expression)
        {
            return new ScenarioUnaryExpression("!", expression);
        }

        private static void Walk(
            ScenarioStatement statement,
            ScenarioExpression parentCondition,
            ScenarioLanguageModel model,
            ref int index)
        {
            var assignment = statement as ScenarioAssignmentStatement;
            if (assignment != null)
            {
                Emit(parentCondition ?? new ScenarioNumberExpression(1.0),
                    new List<ScenarioAssignmentStatement> { assignment }, model, ref index);
                return;
            }

            var block = statement as ScenarioBlockStatement;
            if (block != null)
            {
                var directAssignments = new List<ScenarioAssignmentStatement>();
                foreach (var child in block.Statements)
                {
                    var childAssignment = child as ScenarioAssignmentStatement;
                    if (childAssignment != null) directAssignments.Add(childAssignment);
                }
                Emit(parentCondition ?? new ScenarioNumberExpression(1.0), directAssignments, model, ref index);
                foreach (var child in block.Statements)
                    if (!(child is ScenarioAssignmentStatement))
                        Walk(child, parentCondition, model, ref index);
                return;
            }

            var conditional = statement as ScenarioIfStatement;
            if (conditional != null)
            {
                Walk(conditional.ThenBranch, Combine(parentCondition, conditional.Condition), model, ref index);
                if (conditional.ElseBranch != null)
                    Walk(conditional.ElseBranch, Combine(parentCondition, Negate(conditional.Condition)), model, ref index);
            }
        }

        private static void Emit(
            ScenarioExpression condition,
            IList<ScenarioAssignmentStatement> assignments,
            ScenarioLanguageModel model,
            ref int index)
        {
            if (assignments.Count == 0) return;
            var rule = new ScenarioLanguageRule
            {
                Index = index++,
                Condition = condition,
                RenderedCondition = Render(condition)
            };
            var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectIdentifiers(condition, identifiers);
            rule.Identifiers = identifiers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var item in assignments)
            {
                rule.Actions.Add(new ScenarioLanguageAction
                {
                    TargetId = item.Target,
                    Operator = item.Operator,
                    Expression = item.Expression,
                    Rendered = item.Target + " " + item.Operator + " " + Render(item.Expression)
                });
            }

            model.Rules.Add(rule);
        }

        private static void CollectIdentifiers(ScenarioStatement statement, ISet<string> identifiers)
        {
            var conditional = statement as ScenarioIfStatement;
            if (conditional != null)
            {
                CollectIdentifiers(conditional.Condition, identifiers);
                CollectIdentifiers(conditional.ThenBranch, identifiers);
                if (conditional.ElseBranch != null) CollectIdentifiers(conditional.ElseBranch, identifiers);
                return;
            }

            var assignment = statement as ScenarioAssignmentStatement;
            if (assignment != null)
            {
                identifiers.Add(assignment.Target);
                CollectIdentifiers(assignment.Expression, identifiers);
                return;
            }

            var expressionStatement = statement as ScenarioExpressionStatement;
            if (expressionStatement != null)
            {
                CollectIdentifiers(expressionStatement.Expression, identifiers);
                return;
            }

            var block = statement as ScenarioBlockStatement;
            if (block != null)
                foreach (var child in block.Statements) CollectIdentifiers(child, identifiers);
        }

        private static void CollectIdentifiers(ScenarioExpression expression, ISet<string> identifiers)
        {
            var variable = expression as ScenarioVariableExpression;
            if (variable != null)
            {
                if (!string.Equals(variable.Name, "true", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(variable.Name, "false", StringComparison.OrdinalIgnoreCase))
                    identifiers.Add(variable.Name);
                return;
            }

            var unary = expression as ScenarioUnaryExpression;
            if (unary != null)
            {
                CollectIdentifiers(unary.Expression, identifiers);
                return;
            }

            var binary = expression as ScenarioBinaryExpression;
            if (binary != null)
            {
                CollectIdentifiers(binary.Left, identifiers);
                CollectIdentifiers(binary.Right, identifiers);
                return;
            }

            var call = expression as ScenarioCallExpression;
            if (call != null)
                foreach (var argument in call.Arguments) CollectIdentifiers(argument, identifiers);
        }

        internal static string Render(ScenarioExpression expression)
        {
            var number = expression as ScenarioNumberExpression;
            if (number != null)
                return number.Value.ToString("G", CultureInfo.InvariantCulture);
            var text = expression as ScenarioTextExpression;
            if (text != null)
                return "\"" + text.Value.Replace("\"", "\\\"") + "\"";
            var variable = expression as ScenarioVariableExpression;
            if (variable != null) return variable.Name;
            var unary = expression as ScenarioUnaryExpression;
            if (unary != null) return unary.Operator + Render(unary.Expression);
            var binary = expression as ScenarioBinaryExpression;
            if (binary != null) return "(" + Render(binary.Left) + " " + binary.Operator + " " + Render(binary.Right) + ")";
            var call = expression as ScenarioCallExpression;
            if (call != null) return call.Name + "(" + string.Join(", ", call.Arguments.Select(Render)) + ")";
            return string.Empty;
        }
    }

    internal enum ScenarioValueKind { Unknown, Number, Text, Boolean }

    internal sealed class ScenarioValue
    {
        public ScenarioValueKind Kind;
        public double Number;
        public string Text;
        public bool Boolean;

        public static ScenarioValue Unknown() { return new ScenarioValue { Kind = ScenarioValueKind.Unknown }; }
        public static ScenarioValue FromNumber(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? Unknown()
                : new ScenarioValue { Kind = ScenarioValueKind.Number, Number = value };
        }
        public static ScenarioValue FromText(string value)
        {
            return new ScenarioValue { Kind = ScenarioValueKind.Text, Text = value ?? string.Empty };
        }
        public static ScenarioValue FromBoolean(bool value)
        {
            return new ScenarioValue { Kind = ScenarioValueKind.Boolean, Boolean = value };
        }
    }

    internal static class ScenarioLanguageEvaluator
    {
        public static ScenarioValue Evaluate(ScenarioExpression expression, IDictionary<string, string> variables)
        {
            var number = expression as ScenarioNumberExpression;
            if (number != null) return ScenarioValue.FromNumber(number.Value);

            var text = expression as ScenarioTextExpression;
            if (text != null) return ScenarioValue.FromText(text.Value);

            var variable = expression as ScenarioVariableExpression;
            if (variable != null)
            {
                if (string.Equals(variable.Name, "true", StringComparison.OrdinalIgnoreCase))
                    return ScenarioValue.FromBoolean(true);
                if (string.Equals(variable.Name, "false", StringComparison.OrdinalIgnoreCase))
                    return ScenarioValue.FromBoolean(false);
                string raw;
                if (variables == null || !variables.TryGetValue(variable.Name, out raw))
                    return ScenarioValue.Unknown();
                double numeric;
                if (double.TryParse((raw ?? string.Empty).Trim().Replace(',', '.'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
                    return ScenarioValue.FromNumber(numeric);
                return ScenarioValue.FromText(raw);
            }

            var unary = expression as ScenarioUnaryExpression;
            if (unary != null)
            {
                var value = Evaluate(unary.Expression, variables);
                if (unary.Operator == "!")
                    return value.Kind == ScenarioValueKind.Unknown
                        ? ScenarioValue.Unknown()
                        : ScenarioValue.FromBoolean(!AsBoolean(value));
                if (unary.Operator == "+" || unary.Operator == "-")
                {
                    double n;
                    if (!AsNumber(value, out n)) return ScenarioValue.Unknown();
                    return ScenarioValue.FromNumber(unary.Operator == "-" ? -n : n);
                }
                return ScenarioValue.Unknown();
            }

            var binary = expression as ScenarioBinaryExpression;
            if (binary != null)
                return EvaluateBinary(binary, variables);

            var call = expression as ScenarioCallExpression;
            if (call != null)
                return EvaluateCall(call);

            return ScenarioValue.Unknown();
        }

        private static ScenarioValue EvaluateBinary(ScenarioBinaryExpression expression, IDictionary<string, string> variables)
        {
            var left = Evaluate(expression.Left, variables);
            var right = Evaluate(expression.Right, variables);
            var op = expression.Operator;

            if (op == "+" || op == "-" || op == "*" || op == "/")
            {
                double a, b;
                if (!AsNumber(left, out a) || !AsNumber(right, out b))
                    return ScenarioValue.Unknown();
                if (op == "/" && Math.Abs(b) < 0.0000001)
                    return ScenarioValue.Unknown();
                if (op == "+") return ScenarioValue.FromNumber(a + b);
                if (op == "-") return ScenarioValue.FromNumber(a - b);
                if (op == "*") return ScenarioValue.FromNumber(a * b);
                return ScenarioValue.FromNumber(a / b);
            }

            if (op == "==" || op == "!=" || op == "<" || op == "<=" || op == ">" || op == ">=")
            {
                int comparison;
                if (!TryCompare(left, right, out comparison))
                    return ScenarioValue.Unknown();
                var matches = op == "==" ? comparison == 0 :
                              op == "!=" ? comparison != 0 :
                              op == "<" ? comparison < 0 :
                              op == "<=" ? comparison <= 0 :
                              op == ">" ? comparison > 0 : comparison >= 0;
                return ScenarioValue.FromBoolean(matches);
            }

            if (op == "&" || op == "|")
            {
                if (left.Kind == ScenarioValueKind.Number && right.Kind == ScenarioValueKind.Number)
                {
                    var a = (long)left.Number;
                    var b = (long)right.Number;
                    return ScenarioValue.FromNumber(op == "&" ? (double)(a & b) : (double)(a | b));
                }
                return ScenarioValue.FromBoolean(op == "&"
                    ? AsBoolean(left) && AsBoolean(right)
                    : AsBoolean(left) || AsBoolean(right));
            }

            return ScenarioValue.Unknown();
        }

        private static ScenarioValue EvaluateCall(ScenarioCallExpression expression)
        {
            if (expression.Arguments.Count != 0) return ScenarioValue.Unknown();
            var now = DateTime.Now;
            switch (expression.Name.ToLowerInvariant())
            {
                case "gethours": return ScenarioValue.FromNumber(now.Hour);
                case "getminutes": return ScenarioValue.FromNumber(now.Minute);
                case "getseconds": return ScenarioValue.FromNumber(now.Second);
                case "getmonth": return ScenarioValue.FromNumber(now.Month);
                case "getday": return ScenarioValue.FromNumber(now.Day);
                case "gethhmm": return ScenarioValue.FromNumber(now.Hour * 100 + now.Minute);
                case "gethhmmss": return ScenarioValue.FromNumber(now.Hour * 10000 + now.Minute * 100 + now.Second);
                default: return ScenarioValue.Unknown();
            }
        }

        private static bool TryCompare(ScenarioValue left, ScenarioValue right, out int comparison)
        {
            comparison = 0;
            if (left.Kind == ScenarioValueKind.Unknown || right.Kind == ScenarioValueKind.Unknown)
                return false;

            double a, b;
            if (AsNumber(left, out a) && AsNumber(right, out b))
            {
                comparison = a.CompareTo(b);
                return true;
            }

            string leftText = AsText(left);
            string rightText = AsText(right);
            if (leftText == null || rightText == null) return false;
            comparison = string.Compare(leftText, rightText, StringComparison.Ordinal);
            return true;
        }

        private static bool AsNumber(ScenarioValue value, out double number)
        {
            number = 0;
            if (value == null || value.Kind != ScenarioValueKind.Number) return false;
            number = value.Number;
            return true;
        }

        private static bool AsBoolean(ScenarioValue value)
        {
            if (value == null) return false;
            if (value.Kind == ScenarioValueKind.Boolean) return value.Boolean;
            if (value.Kind == ScenarioValueKind.Number) return Math.Abs(value.Number) > 0.000001;
            if (value.Kind == ScenarioValueKind.Text)
                return !string.IsNullOrWhiteSpace(value.Text) &&
                       !string.Equals(value.Text, "0", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(value.Text, "false", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private static string AsText(ScenarioValue value)
        {
            if (value == null || value.Kind == ScenarioValueKind.Unknown) return null;
            if (value.Kind == ScenarioValueKind.Text) return value.Text;
            if (value.Kind == ScenarioValueKind.Boolean) return value.Boolean ? "true" : "false";
            return value.Number.ToString("G", CultureInfo.InvariantCulture);
        }

        public static bool IsTruthy(ScenarioValue value)
        {
            return AsBoolean(value);
        }

        public static string AsComparableText(ScenarioValue value)
        {
            if (value == null || value.Kind == ScenarioValueKind.Unknown) return null;
            if (value.Kind == ScenarioValueKind.Boolean) return value.Boolean ? "1" : "0";
            if (value.Kind == ScenarioValueKind.Number)
                return value.Number.ToString("G", CultureInfo.InvariantCulture);
            return value.Text;
        }

        public static bool ValuesEquivalent(string actual, string expected)
        {
            var a = (actual ?? string.Empty).Trim();
            var e = (expected ?? string.Empty).Trim();
            double an, en;
            if (double.TryParse(a.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out an) &&
                double.TryParse(e.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out en))
                return Math.Abs(an - en) < 0.000001;
            return string.Equals(a, e, StringComparison.Ordinal);
        }
    }
}