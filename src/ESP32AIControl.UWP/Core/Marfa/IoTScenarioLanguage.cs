using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ESP32AIControl.UWP.Core.Marfa
{
    // C# 7.3 compatible port of the Android Marfa scenario expression language.
    public abstract class IoTExpr
    {
        public sealed class NumberLiteral : IoTExpr { public double Value; public NumberLiteral(double value) { Value = value; } }
        public sealed class StringLiteral : IoTExpr { public string Value; public StringLiteral(string value) { Value = value; } }
        public sealed class Variable : IoTExpr { public string Name; public Variable(string name) { Name = name; } }
        public sealed class Unary : IoTExpr { public string Operator; public IoTExpr Expression; public Unary(string op, IoTExpr e) { Operator = op; Expression = e; } }
        public sealed class Binary : IoTExpr { public string Operator; public IoTExpr Left; public IoTExpr Right; public Binary(string op, IoTExpr l, IoTExpr r) { Operator = op; Left = l; Right = r; } }
        public sealed class Call : IoTExpr { public string Name; public IList<IoTExpr> Args; public Call(string name, IList<IoTExpr> args) { Name = name; Args = args; } }
    }

    public abstract class IoTStatement
    {
        public sealed class If : IoTStatement { public IoTExpr Condition; public IoTStatement ThenBranch; public IoTStatement ElseBranch; public If(IoTExpr c, IoTStatement t, IoTStatement e) { Condition=c; ThenBranch=t; ElseBranch=e; } }
        public sealed class Assignment : IoTStatement { public string Target; public string Operator; public IoTExpr Expression; public Assignment(string t,string o,IoTExpr e){Target=t;Operator=o;Expression=e;} }
        public sealed class ExpressionStatement : IoTStatement { public IoTExpr Expression; public ExpressionStatement(IoTExpr e){Expression=e;} }
        public sealed class Block : IoTStatement { public IList<IoTStatement> Statements; public Block(IList<IoTStatement> s){Statements=s;} }
    }

    public sealed class IoTScenarioParseResult
    {
        public IList<IoTStatement> Statements { get; set; }
        public IList<string> Errors { get; set; }
        public ISet<string> ReferencedIdentifiers { get; set; }
        public IoTScenarioParseResult() { Statements=new List<IoTStatement>(); Errors=new List<string>(); ReferencedIdentifiers=new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }
    public sealed class DeviceScenarioCondition { public IoTExpr Expression {get;set;} public string Rendered {get;set;} public ISet<string> Identifiers {get;set;} }
    public sealed class DeviceScenarioAction { public string TargetId {get;set;} public string Operator {get;set;} public IoTExpr Expression {get;set;} public string Rendered {get;set;} }
    public sealed class DeviceScenarioRule { public int Index {get;set;} public DeviceScenarioCondition Condition {get;set;} public IList<DeviceScenarioAction> Actions {get;set;} }
    public sealed class DeviceScenarioModel
    {
        public IList<DeviceScenarioRule> Rules {get;set;} = new List<DeviceScenarioRule>();
        public ISet<string> Identifiers {get;set;} = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public IList<string> ParserErrors {get;set;} = new List<string>();
        public IList<IoTStatement> Statements {get;set;} = new List<IoTStatement>();
        public bool Valid { get { return ParserErrors.Count==0 && Rules.Count>0; } }
    }

    public static class IoTScenarioParser
    {
        private enum T { Id, Num, Str, If, Then, Else, Lp, Rp, Lb, Rb, Comma, Semi, Assign, SilentAssign, Eq, Ne, Lt, Le, Gt, Ge, Plus, Minus, Mul, Div, And, Or, Bang, End }
        private sealed class Token { public T Type; public string Text; public int Pos; public Token(T t,string s,int p){Type=t;Text=s;Pos=p;} }
        private sealed class Lexer
        {
            private readonly string s; private int i; public readonly List<string> Errors=new List<string>();
            public Lexer(string source){s=source??"";}
            public List<Token> Run()
            {
                var a=new List<Token>();
                while(i<s.Length)
                {
                    char c=s[i]; if(Char.IsWhiteSpace(c)){i++;continue;}
                    if(c=='#'){while(i<s.Length&&s[i]!='\n')i++;continue;}
                    if(c=='"'||c=='\'')
                    {
                        int start=i++; char q=c; var b=new StringBuilder(); bool closed=false;
                        while(i<s.Length){char x=s[i++]; if(x=='\\'&&i<s.Length){char n=s[i++]; b.Append(n=='n'?'\n':n=='r'?'\r':n=='t'?'\t':n);}
                            else if(x==q){closed=true;break;} else b.Append(x);}
                        if(closed)a.Add(new Token(T.Str,b.ToString(),start)); else Errors.Add("Незакрытая строка в позиции "+start);
                        continue;
                    }
                    if(Char.IsDigit(c)||(c=='.'&&i+1<s.Length&&Char.IsDigit(s[i+1])))
                    {int st=i;bool dot=false;while(i<s.Length&&(Char.IsDigit(s[i])||(s[i]=='.'&&!dot))){if(s[i]=='.')dot=true;i++;}a.Add(new Token(T.Num,s.Substring(st,i-st),st));continue;}
                    if(Char.IsLetter(c)||c=='_')
                    {int st=i++;while(i<s.Length&&(Char.IsLetterOrDigit(s[i])||s[i]=='_'||s[i]=='.'))i++;string w=s.Substring(st,i-st);string l=w.ToLowerInvariant();a.Add(new Token(l=="if"?T.If:l=="then"?T.Then:l=="else"?T.Else:T.Id,w,st));continue;}
                    int p=i;
                    if(i+1<s.Length){string two=s.Substring(i,2);T? tt=two=="<="?T.Le:two==">="?T.Ge:two=="=="?T.Eq:two=="!="?T.Ne:two==":="?T.SilentAssign:null;if(tt.HasValue){a.Add(new Token(tt.Value,two,p));i+=2;continue;}}
                    T type;
                    switch(c){case '=':type=T.Assign;break;case '<':type=T.Lt;break;case '>':type=T.Gt;break;case '+':type=T.Plus;break;case '-':type=T.Minus;break;case '*':type=T.Mul;break;case '/':type=T.Div;break;case '&':type=T.And;break;case '|':type=T.Or;break;case '!':type=T.Bang;break;case '(':type=T.Lp;break;case ')':type=T.Rp;break;case '{':type=T.Lb;break;case '}':type=T.Rb;break;case ',':type=T.Comma;break;case ';':type=T.Semi;break;default:Errors.Add("Неизвестный символ '"+c+"' в позиции "+i);i++;continue;}
                    a.Add(new Token(type,c.ToString(),i++));
                }
                a.Add(new Token(T.End,"",s.Length));return a;
            }
        }
        private sealed class Parser
        {
            private readonly IList<Token> ts; private readonly List<string> errors; private int i;
            public Parser(IList<Token> tokens,List<string> errs){ts=tokens;errors=errs;}
            private Token Cur {get{return ts[Math.Min(i,ts.Count-1)];}}
            private Token Prev {get{return ts[Math.Max(0,i-1)];}}
            private bool Check(T t){return Cur.Type==t;}
            private bool Match(T t){if(!Check(t))return false;i++;return true;}
            private Token Advance(){Token t=Cur;if(i<ts.Count-1)i++;return t;}
            private void Error(string s){errors.Add(s+" (позиция "+Cur.Pos+")");}
            public IoTScenarioParseResult Run()
            {
                var result=new IoTScenarioParseResult(); while(!Check(T.End)){if(Match(T.Semi))continue;int before=i;var st=Statement();if(st!=null)result.Statements.Add(st);if(i==before)i++;}
                result.Errors=errors; foreach(var st in result.Statements)Collect(st,result.ReferencedIdentifiers);return result;
            }
            private IoTStatement Statement()
            {
                if(Match(T.If)){var condition=Expr();if(!Match(T.Then)){Error("Ожидалось THEN после условия IF");Sync();return null;}var yes=Statement();if(yes==null){Error("Не удалось разобрать ветвь THEN");return null;}var no=Match(T.Else)?Statement():null;return new IoTStatement.If(condition,yes,no);}
                if(Check(T.Lb)){Advance();var list=new List<IoTStatement>();while(!Check(T.Rb)&&!Check(T.End)){if(Match(T.Semi))continue;int before=i;var s=Statement();if(s!=null)list.Add(s);if(before==i)i++;}if(!Match(T.Rb))Error("Ожидалась '}'");Match(T.Semi);return new IoTStatement.Block(list);}
                var left=Expr();if(Match(T.Assign)||Match(T.SilentAssign)){string op=Prev.Text;var v=left as IoTExpr.Variable;if(v==null){Error("Левая часть присваивания должна быть идентификатором");return null;}var rhs=Expr();Match(T.Semi);return new IoTStatement.Assignment(v.Name,op,rhs);}Match(T.Semi);return new IoTStatement.ExpressionStatement(left);
            }
            private void Sync(){while(!Check(T.End)){if(Match(T.Semi))return;if(Check(T.If)||Check(T.Rb)||Check(T.Else))return;Advance();}}
            private IoTExpr Expr(){return Binary(0);}
            private int Prec(T t){switch(t){case T.Or:return 1;case T.And:return 2;case T.Eq:case T.Ne:return 3;case T.Lt:case T.Le:case T.Gt:case T.Ge:return 4;case T.Plus:case T.Minus:return 5;case T.Mul:case T.Div:return 6;default:return -1;}}
            private IoTExpr Binary(int min){var left=Unary();while(Prec(Cur.Type)>=min){var op=Advance();left=new IoTExpr.Binary(op.Text,left,Binary(Prec(op.Type)+1));}return left;}
            private IoTExpr Unary(){if(Match(T.Minus))return new IoTExpr.Unary("-",Unary());if(Match(T.Plus))return new IoTExpr.Unary("+",Unary());if(Match(T.Bang))return new IoTExpr.Unary("!",Unary());return Primary();}
            private IoTExpr Primary()
            {
                Token t=Cur;if(Match(T.Num)){double n;Double.TryParse(t.Text,NumberStyles.Any,CultureInfo.InvariantCulture,out n);return new IoTExpr.NumberLiteral(n);}
                if(Match(T.Str))return new IoTExpr.StringLiteral(t.Text);
                if(Match(T.Id)){if(Match(T.Lp)){var args=new List<IoTExpr>();if(!Check(T.Rp)){do{args.Add(Expr());}while(Match(T.Comma));}if(!Match(T.Rp))Error("Ожидалась ')' после вызова");return new IoTExpr.Call(t.Text,args);}return new IoTExpr.Variable(t.Text);}
                if(Match(T.Lp)){var e=Expr();if(!Match(T.Rp))Error("Ожидалась ')'");return e;}
                Error("Ожидалось выражение");if(!Check(T.End))Advance();return new IoTExpr.NumberLiteral(0);
            }
            private static void CollectExpr(IoTExpr e,ISet<string> ids){var v=e as IoTExpr.Variable;if(v!=null){ids.Add(v.Name);return;}var u=e as IoTExpr.Unary;if(u!=null){CollectExpr(u.Expression,ids);return;}var b=e as IoTExpr.Binary;if(b!=null){CollectExpr(b.Left,ids);CollectExpr(b.Right,ids);return;}var c=e as IoTExpr.Call;if(c!=null)foreach(var x in c.Args)CollectExpr(x,ids);}
            private static void Collect(IoTStatement s,ISet<string> ids){var a=s as IoTStatement.Assignment;if(a!=null){ids.Add(a.Target);CollectExpr(a.Expression,ids);return;}var e=s as IoTStatement.ExpressionStatement;if(e!=null){CollectExpr(e.Expression,ids);return;}var f=s as IoTStatement.If;if(f!=null){CollectExpr(f.Condition,ids);Collect(f.ThenBranch,ids);if(f.ElseBranch!=null)Collect(f.ElseBranch,ids);return;}var block=s as IoTStatement.Block;if(block!=null)foreach(var x in block.Statements)Collect(x,ids);}
        }
        public static IoTScenarioParseResult Parse(string source)
        {
            string s=(source??"").Trim();var marker=Regex.Match(s,@"(?m)^\s*scenario=>");if(marker.Success)s=s.Substring(marker.Index+marker.Length);
            var lexer=new Lexer(s);var tokens=lexer.Run();return new Parser(tokens,lexer.Errors).Run();
        }
    }

    public static class IoTScenarioSemanticAnalyzer
    {
        public static DeviceScenarioModel Analyze(IoTScenarioParseResult parsed)
        {
            var model=new DeviceScenarioModel{Identifiers=new HashSet<string>(parsed.ReferencedIdentifiers,StringComparer.OrdinalIgnoreCase),ParserErrors=parsed.Errors,Statements=parsed.Statements};
            Action<IoTExpr,IList<IoTStatement.Assignment>> emit=(condition,assignments)=>{
                if(assignments.Count==0)return;var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);CollectExpr(condition,ids);
                var rule=new DeviceScenarioRule{Index=model.Rules.Count,Condition=new DeviceScenarioCondition{Expression=condition,Rendered=Render(condition),Identifiers=ids},Actions=new List<DeviceScenarioAction>()};
                foreach(var a in assignments)rule.Actions.Add(new DeviceScenarioAction{TargetId=a.Target,Operator=a.Operator,Expression=a.Expression,Rendered=a.Target+" "+a.Operator+" "+Render(a.Expression)});
                model.Rules.Add(rule);
            };
            Action<IoTStatement,IoTExpr> walk=null;
            walk=(st,parent)=>{
                var a=st as IoTStatement.Assignment;if(a!=null){emit(parent??(IoTExpr)new IoTExpr.NumberLiteral(1),new List<IoTStatement.Assignment>{a});return;}
                var b=st as IoTStatement.Block;if(b!=null){var assigns=b.Statements.OfType<IoTStatement.Assignment>().ToList();emit(parent??new IoTExpr.NumberLiteral(1),assigns);foreach(var x in b.Statements.Where(x=>!(x is IoTStatement.Assignment)))walk(x,parent);return;}
                var f=st as IoTStatement.If;if(f!=null){walk(f.ThenBranch,parent==null?f.Condition:new IoTExpr.Binary("&",parent,f.Condition));if(f.ElseBranch!=null)walk(f.ElseBranch,parent==null?(IoTExpr)new IoTExpr.Unary("!",f.Condition):new IoTExpr.Binary("&",parent,new IoTExpr.Unary("!",f.Condition)));}
            };
            foreach(var s in parsed.Statements)walk(s,null);return model;
        }
        private static void CollectExpr(IoTExpr e,ISet<string> ids){var v=e as IoTExpr.Variable;if(v!=null){ids.Add(v.Name);return;}var u=e as IoTExpr.Unary;if(u!=null){CollectExpr(u.Expression,ids);return;}var b=e as IoTExpr.Binary;if(b!=null){CollectExpr(b.Left,ids);CollectExpr(b.Right,ids);return;}var c=e as IoTExpr.Call;if(c!=null)foreach(var x in c.Args)CollectExpr(x,ids);}
        public static string Render(IoTExpr e)
        {
            var n=e as IoTExpr.NumberLiteral;if(n!=null)return n.Value.ToString("0.################",CultureInfo.InvariantCulture);
            var s=e as IoTExpr.StringLiteral;if(s!=null)return "\""+(s.Value??"").Replace("\"","\\\"")+"\"";
            var v=e as IoTExpr.Variable;if(v!=null)return v.Name;
            var u=e as IoTExpr.Unary;if(u!=null)return u.Operator+Render(u.Expression);
            var b=e as IoTExpr.Binary;if(b!=null)return "("+Render(b.Left)+" "+b.Operator+" "+Render(b.Right)+")";
            var c=e as IoTExpr.Call;if(c!=null)return c.Name+"("+String.Join(", ",c.Args.Select(Render))+")";
            return "";
        }
    }

    public abstract class IoTValue
    {
        public sealed class Number : IoTValue { public double Value; public Number(double v){Value=v;} }
        public sealed class Text : IoTValue { public string Value; public Text(string v){Value=v;} }
        public sealed class BooleanValue : IoTValue { public bool Value; public BooleanValue(bool v){Value=v;} }
        public sealed class Unknown : IoTValue { }
    }
    public sealed class IoTScenarioEvaluationContext
    {
        public IDictionary<string,string> Variables {get;set;}
        public IDictionary<string,Func<IList<IoTValue>,IoTValue>> Functions {get;set;}
        public IoTScenarioEvaluationContext(){Variables=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);Functions=new Dictionary<string,Func<IList<IoTValue>,IoTValue>>(StringComparer.OrdinalIgnoreCase);}
    }
    public static class IoTScenarioEvaluator
    {
        public static IoTValue Evaluate(IoTExpr e,IoTScenarioEvaluationContext c)
        {
            var n=e as IoTExpr.NumberLiteral;if(n!=null)return new IoTValue.Number(n.Value);
            var s=e as IoTExpr.StringLiteral;if(s!=null)return new IoTValue.Text(s.Value);
            var v=e as IoTExpr.Variable;if(v!=null){string raw;if(!c.Variables.TryGetValue(v.Name,out raw)){if(v.Name.Equals("true",StringComparison.OrdinalIgnoreCase))return new IoTValue.BooleanValue(true);if(v.Name.Equals("false",StringComparison.OrdinalIgnoreCase))return new IoTValue.BooleanValue(false);return new IoTValue.Unknown();}double d;if(Double.TryParse((raw??"").Replace(',','.'),NumberStyles.Any,CultureInfo.InvariantCulture,out d))return new IoTValue.Number(d);return new IoTValue.Text(raw);}
            var u=e as IoTExpr.Unary;if(u!=null){var x=Evaluate(u.Expression,c);double d;if(u.Operator=="!" )return x is IoTValue.Unknown? (IoTValue)new IoTValue.Unknown():new IoTValue.BooleanValue(!Truthy(x));if(!Number(x,out d))return new IoTValue.Unknown();return new IoTValue.Number(u.Operator=="-"?-d:d);}
            var call=e as IoTExpr.Call;if(call!=null){Func<IList<IoTValue>,IoTValue> fn;if(c.Functions.TryGetValue(call.Name,out fn))return fn(call.Args.Select(x=>Evaluate(x,c)).ToList());return Builtin(call.Name);}
            var b=e as IoTExpr.Binary;if(b==null)return new IoTValue.Unknown();var l=Evaluate(b.Left,c);var r=Evaluate(b.Right,c);double a,z;
            if(b.Operator=="+"||b.Operator=="-"||b.Operator=="*"||b.Operator=="/"){if(!Number(l,out a)||!Number(r,out z)||(b.Operator=="/"&&Math.Abs(z)<0.0000001))return new IoTValue.Unknown();return new IoTValue.Number(b.Operator=="+"?a+z:b.Operator=="-"?a-z:b.Operator=="*"?a*z:a/z);}
            if(b.Operator=="&"||b.Operator=="|"){if(l is IoTValue.Number&&r is IoTValue.Number){Number(l,out a);Number(r,out z);long x=(long)a,y=(long)z;return new IoTValue.Number(b.Operator=="&"?(x&y):(x|y));}return new IoTValue.BooleanValue(b.Operator=="&"?(Truthy(l)&&Truthy(r)):(Truthy(l)||Truthy(r)));}
            int cmp;if(!Compare(l,r,out cmp))return new IoTValue.Unknown();return new IoTValue.BooleanValue(b.Operator=="=="?cmp==0:b.Operator=="!="?cmp!=0:b.Operator=="<"?cmp<0:b.Operator=="<="?cmp<=0:b.Operator==">"?cmp>0:cmp>=0);
        }
        public static bool IsTruthy(IoTValue v){return Truthy(v);}
        private static bool Number(IoTValue v,out double n){var x=v as IoTValue.Number;if(x!=null){n=x.Value;return true;}n=0;return false;}
        private static bool Truthy(IoTValue v){var b=v as IoTValue.BooleanValue;if(b!=null)return b.Value;var n=v as IoTValue.Number;if(n!=null)return Math.Abs(n.Value)>0.000001;var s=v as IoTValue.Text;return s!=null&&!String.IsNullOrWhiteSpace(s.Value)&&s.Value!="0"&&!s.Value.Equals("false",StringComparison.OrdinalIgnoreCase);}
        private static bool Compare(IoTValue l,IoTValue r,out int cmp){cmp=0;if(l is IoTValue.Unknown||r is IoTValue.Unknown)return false;double a,b;if(Number(l,out a)&&Number(r,out b)){cmp=a.CompareTo(b);return true;}string x=Text(l),y=Text(r);if(x==null||y==null)return false;cmp=String.CompareOrdinal(x,y);return true;}
        private static string Text(IoTValue v){var s=v as IoTValue.Text;if(s!=null)return s.Value;var n=v as IoTValue.Number;if(n!=null)return n.Value.ToString(CultureInfo.InvariantCulture);var b=v as IoTValue.BooleanValue;if(b!=null)return b.Value.ToString();return null;}
        private static IoTValue Builtin(string name){DateTime n=DateTime.Now;switch((name??"").ToLowerInvariant()){case "gethours":return new IoTValue.Number(n.Hour);case "getminutes":return new IoTValue.Number(n.Minute);case "getseconds":return new IoTValue.Number(n.Second);case "getmonth":return new IoTValue.Number(n.Month);case "getday":return new IoTValue.Number(n.Day);case "gethhmm":return new IoTValue.Number(n.Hour*100+n.Minute);case "gethhmmss":return new IoTValue.Number(n.Hour*10000+n.Minute*100+n.Second);default:return new IoTValue.Unknown();}}
    }

    public sealed class ScenarioPrerequisite { public string DeviceId {get;set;} public string WidgetId {get;set;} public string Value {get;set;} public string Reason {get;set;} }
    public sealed class ScenarioCommandPlan { public IList<LocalCommandActionItem> Actions {get;set;} = new List<LocalCommandActionItem>(); public IList<ScenarioPrerequisite> Prerequisites {get;set;} = new List<ScenarioPrerequisite>(); public string BlockedReason {get;set;} }
}