using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Turns a SQL WHERE clause into an <see cref="AlertConditionNode"/> tree -
/// the equivalent of the LibreNMS web editor's "Import from → SQL Query",
/// which hands the text to jQuery QueryBuilder's <c>setRulesFromSQL</c>.
/// Accepts either a bare condition (<c>devices.status = 0 AND ...</c>) or a
/// full <c>SELECT ... WHERE ...</c> as shown in LibreNMS's rule list, in
/// which case the device-join clauses LibreNMS prepends
/// (<c>devices.device_id = ?</c>, <c>x.device_id = y.device_id</c>) are
/// dropped since they aren't conditions the user wrote.
/// </summary>
public static class AlertRuleSqlImporter
{
    /// <summary>
    /// Parses <paramref name="sql"/>; throws <see cref="FormatException"/>
    /// with a position-annotated message when it can't.
    /// </summary>
    public static AlertConditionNode Parse(string sql)
    {
        var text = StripSelect(sql);
        var parser = new Parser(Tokenizer.Tokenize(text));
        var node = parser.ParseExpression();
        parser.ExpectEnd();

        // Always hand back a group at the top, the shape LibreNMS stores.
        if (!node.IsGroup)
        {
            node = new AlertConditionNode { Condition = "AND", Rules = new List<AlertConditionNode> { node } };
        }

        var pruned = Prune(node) ?? new AlertConditionNode { Condition = "AND", Rules = new List<AlertConditionNode>() };
        return pruned.IsGroup ? pruned : new AlertConditionNode { Condition = "AND", Rules = new List<AlertConditionNode> { pruned } };
    }

    /// <summary>
    /// The web editor's "Import from → Old Format": LibreNMS's pre-QueryBuilder
    /// rule syntax (<c>%devices.status = "0" &amp;&amp; %macros.device_up = "1"</c>),
    /// converted with exactly the substitutions its own JavaScript applies
    /// before handing the result to the SQL parser.
    /// </summary>
    public static AlertConditionNode ParseOldFormat(string rule)
    {
        var converted = rule
            .Replace("&&", "AND")
            .Replace("||", "OR")
            .Replace("%", string.Empty)
            .Replace('"', '\'')
            .Replace("~", "REGEXP")
            .Replace("@", ".*");

        return Parse(converted);
    }

    private static string StripSelect(string sql)
    {
        var trimmed = sql.Trim().TrimEnd(';');
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var match = Regex.Match(trimmed, @"\bWHERE\b", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new FormatException("This looks like a SELECT statement but has no WHERE clause to import.");
        }

        return trimmed[(match.Index + match.Length)..];
    }

    /// <summary>Removes join-clause leaves, groups left empty by doing so, and single-group wrappers.</summary>
    private static AlertConditionNode? Prune(AlertConditionNode node)
    {
        if (!node.IsGroup)
        {
            return IsJoinClause(node) ? null : node;
        }

        var kept = node.Rules!
            .Select(Prune)
            .Where(child => child is not null)
            .Select(child => child!)
            .ToList();

        if (kept.Count == 0)
        {
            return null;
        }

        // A group whose only member is another group is just redundant
        // parentheses - LibreNMS wraps every query as "(joins) AND ((rule))",
        // which would otherwise import as a needless extra nesting level.
        if (kept.Count == 1 && kept[0].IsGroup)
        {
            return kept[0];
        }

        node.Rules = kept;
        return node;
    }

    private static bool IsJoinClause(AlertConditionNode leaf) =>
        leaf.Operator == "equal"
        && leaf.Field is not null
        && leaf.Field.EndsWith(".device_id", StringComparison.OrdinalIgnoreCase)
        && leaf.ValueList.Count == 1
        && (leaf.ValueList[0] == "?" || leaf.ValueList[0].EndsWith(".device_id", StringComparison.OrdinalIgnoreCase));

    private enum TokenKind { Identifier, String, Number, Operator, LeftParen, RightParen, Comma, Placeholder, End }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private static class Tokenizer
    {
        public static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var i = 0;

            while (i < text.Length)
            {
                var c = text[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '(') { tokens.Add(new Token(TokenKind.LeftParen, "(", i)); i++; continue; }
                if (c == ')') { tokens.Add(new Token(TokenKind.RightParen, ")", i)); i++; continue; }
                if (c == ',') { tokens.Add(new Token(TokenKind.Comma, ",", i)); i++; continue; }
                if (c == '?') { tokens.Add(new Token(TokenKind.Placeholder, "?", i)); i++; continue; }

                if (c is '\'' or '"')
                {
                    var start = i;
                    tokens.Add(new Token(TokenKind.String, ReadString(text, ref i, c), start));
                    continue;
                }

                if (c == '`')
                {
                    var start = i + 1;
                    var end = text.IndexOf('`', start);
                    if (end < 0)
                    {
                        throw new FormatException($"Unterminated backtick identifier at position {i}.");
                    }

                    tokens.Add(new Token(TokenKind.Identifier, text[start..end], i));
                    i = end + 1;
                    continue;
                }

                if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    var start = i;
                    i++;
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Number, text[start..i], start));
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Identifier, text[start..i], start));
                    continue;
                }

                var op = c switch
                {
                    '=' => "=",
                    '<' when Peek(text, i + 1) == '=' => "<=",
                    '<' when Peek(text, i + 1) == '>' => "<>",
                    '<' => "<",
                    '>' when Peek(text, i + 1) == '=' => ">=",
                    '>' => ">",
                    '!' when Peek(text, i + 1) == '=' => "!=",
                    _ => null,
                };

                if (op is null)
                {
                    throw new FormatException($"Unexpected '{c}' at position {i}.");
                }

                tokens.Add(new Token(TokenKind.Operator, op, i));
                i += op.Length;
            }

            tokens.Add(new Token(TokenKind.End, string.Empty, text.Length));
            return tokens;
        }

        private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';

        private static string ReadString(string text, ref int i, char quote)
        {
            var builder = new StringBuilder();
            var start = i;
            i++; // opening quote

            while (i < text.Length)
            {
                var c = text[i];

                if (c == '\\' && i + 1 < text.Length)
                {
                    builder.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == quote)
                {
                    // A doubled quote is an escaped quote in SQL.
                    if (i + 1 < text.Length && text[i + 1] == quote)
                    {
                        builder.Append(quote);
                        i += 2;
                        continue;
                    }

                    i++;
                    return builder.ToString();
                }

                builder.Append(c);
                i++;
            }

            throw new FormatException($"Unterminated string starting at position {start}.");
        }
    }

    private sealed class Parser
    {
        /// <summary>Pasted text is untrusted input; without a cap, a wall of "(" would recurse until the stack overflowed, which can't be caught.</summary>
        private const int MaxDepth = 64;

        private readonly List<Token> _tokens;
        private int _index;
        private int _depth;

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
        }

        private Token Current => _tokens[_index];

        private bool IsKeyword(string keyword) =>
            Current.Kind == TokenKind.Identifier && string.Equals(Current.Text, keyword, StringComparison.OrdinalIgnoreCase);

        private bool TakeKeyword(string keyword)
        {
            if (!IsKeyword(keyword))
            {
                return false;
            }

            _index++;
            return true;
        }

        private Token Take()
        {
            var token = Current;
            _index++;
            return token;
        }

        public void ExpectEnd()
        {
            if (Current.Kind != TokenKind.End)
            {
                throw new FormatException($"Unexpected '{Current.Text}' at position {Current.Position}.");
            }
        }

        // expr := term (OR term)*
        public AlertConditionNode ParseExpression()
        {
            var first = ParseTerm();
            if (!IsKeyword("OR"))
            {
                return first;
            }

            var group = new AlertConditionNode { Condition = "OR", Rules = new List<AlertConditionNode> { first } };
            while (TakeKeyword("OR"))
            {
                group.Rules.Add(ParseTerm());
            }

            return group;
        }

        // term := factor (AND factor)*
        private AlertConditionNode ParseTerm()
        {
            var first = ParseFactor();
            if (!IsKeyword("AND"))
            {
                return first;
            }

            var group = new AlertConditionNode { Condition = "AND", Rules = new List<AlertConditionNode> { first } };
            while (TakeKeyword("AND"))
            {
                group.Rules.Add(ParseFactor());
            }

            return group;
        }

        // factor := NOT factor | '(' expr ')' | comparison
        private AlertConditionNode ParseFactor()
        {
            if (TakeKeyword("NOT"))
            {
                return Negate(ParseFactor());
            }

            if (Current.Kind == TokenKind.LeftParen)
            {
                if (++_depth > MaxDepth)
                {
                    throw new FormatException($"Too deeply nested (more than {MaxDepth} levels of parentheses).");
                }

                Take();
                var inner = ParseExpression();
                if (Current.Kind != TokenKind.RightParen)
                {
                    throw new FormatException($"Expected ')' at position {Current.Position}.");
                }

                Take();
                _depth--;
                return inner;
            }

            return ParseComparison();
        }

        private AlertConditionNode ParseComparison()
        {
            if (Current.Kind != TokenKind.Identifier)
            {
                throw new FormatException($"Expected a field name (e.g. devices.status) at position {Current.Position}, found '{Current.Text}'.");
            }

            var field = Take().Text;

            if (TakeKeyword("IS"))
            {
                var negated = TakeKeyword("NOT");
                if (!TakeKeyword("NULL"))
                {
                    throw new FormatException($"Expected NULL after IS at position {Current.Position}.");
                }

                return Leaf(field, negated ? "is_not_null" : "is_null", null);
            }

            var not = TakeKeyword("NOT");

            if (TakeKeyword("BETWEEN"))
            {
                var low = ReadLiteral();
                if (!TakeKeyword("AND"))
                {
                    throw new FormatException($"Expected AND in BETWEEN at position {Current.Position}.");
                }

                var high = ReadLiteral();
                return Leaf(field, not ? "not_between" : "between", AlertConditionNode.ArrayValue(new[] { low, high }));
            }

            if (TakeKeyword("IN"))
            {
                if (Current.Kind != TokenKind.LeftParen)
                {
                    throw new FormatException($"Expected '(' after IN at position {Current.Position}.");
                }

                Take();
                var values = new List<string>();
                while (true)
                {
                    values.Add(ReadLiteral());
                    if (Current.Kind == TokenKind.Comma)
                    {
                        Take();
                        continue;
                    }

                    if (Current.Kind == TokenKind.RightParen)
                    {
                        Take();
                        break;
                    }

                    throw new FormatException($"Expected ',' or ')' in IN list at position {Current.Position}.");
                }

                return Leaf(field, not ? "not_in" : "in", AlertConditionNode.ArrayValue(values));
            }

            if (TakeKeyword("LIKE"))
            {
                return LikeLeaf(field, ReadLiteral(), not);
            }

            if (TakeKeyword("REGEXP") || TakeKeyword("RLIKE"))
            {
                return Leaf(field, not ? "not_regex" : "regex", AlertConditionNode.ScalarValue(ReadLiteral()));
            }

            if (not)
            {
                throw new FormatException($"Unexpected NOT at position {Current.Position}.");
            }

            if (Current.Kind != TokenKind.Operator)
            {
                throw new FormatException($"Expected a comparison operator after '{field}' at position {Current.Position}.");
            }

            var op = Take().Text;

            // The right-hand side may be a field or a placeholder too -
            // LibreNMS's own join clauses are "a.device_id = b.device_id" and
            // "devices.device_id = ?" - kept verbatim so Prune() can recognise
            // and drop them.
            var value = Current.Kind is TokenKind.Identifier or TokenKind.Placeholder
                ? Take().Text
                : ReadLiteral();

            var operatorName = op switch
            {
                "=" when value.Length == 0 => "is_empty",
                "!=" or "<>" when value.Length == 0 => "is_not_empty",
                "=" => "equal",
                "!=" or "<>" => "not_equal",
                "<" => "less",
                "<=" => "less_or_equal",
                ">" => "greater",
                ">=" => "greater_or_equal",
                _ => throw new FormatException($"Unsupported operator '{op}'."),
            };

            return Leaf(field, operatorName, operatorName is "is_empty" or "is_not_empty" ? null : AlertConditionNode.ScalarValue(value));
        }

        private string ReadLiteral()
        {
            if (Current.Kind is TokenKind.String or TokenKind.Number)
            {
                return Take().Text;
            }

            throw new FormatException($"Expected a value at position {Current.Position}, found '{Current.Text}'.");
        }

        private static AlertConditionNode LikeLeaf(string field, string pattern, bool negated)
        {
            var starts = pattern.StartsWith('%');
            var ends = pattern.EndsWith('%');
            var core = pattern.Trim('%');

            var op = (starts, ends) switch
            {
                (true, true) => "contains",
                (false, true) => "begins_with",
                (true, false) => "ends_with",
                _ => "equal",
            };

            if (negated)
            {
                op = op == "equal" ? "not_equal" : "not_" + op;
            }

            return Leaf(field, op, AlertConditionNode.ScalarValue(core));
        }

        private static AlertConditionNode Negate(AlertConditionNode node)
        {
            if (node.IsGroup)
            {
                // De Morgan: NOT (a AND b) == (NOT a) OR (NOT b)
                node.Condition = node.Condition == "AND" ? "OR" : "AND";
                node.Rules = node.Rules!.Select(Negate).ToList();
                return node;
            }

            node.Operator = node.Operator switch
            {
                "equal" => "not_equal",
                "not_equal" => "equal",
                "less" => "greater_or_equal",
                "less_or_equal" => "greater",
                "greater" => "less_or_equal",
                "greater_or_equal" => "less",
                "between" => "not_between",
                "not_between" => "between",
                "in" => "not_in",
                "not_in" => "in",
                "begins_with" => "not_begins_with",
                "not_begins_with" => "begins_with",
                "contains" => "not_contains",
                "not_contains" => "contains",
                "ends_with" => "not_ends_with",
                "not_ends_with" => "ends_with",
                "is_empty" => "is_not_empty",
                "is_not_empty" => "is_empty",
                "is_null" => "is_not_null",
                "is_not_null" => "is_null",
                "regex" => "not_regex",
                "not_regex" => "regex",
                var other => throw new FormatException($"Can't negate operator '{other}'."),
            };

            return node;
        }

        private static AlertConditionNode Leaf(string field, string op, JsonNode? value)
        {
            var known = AlertConditionFields.Resolve(field);
            return new AlertConditionNode
            {
                Id = field,
                Field = field,
                Type = known.Type,
                Input = known.Input,
                Operator = op,
                Value = value,
                Valid = true,
            };
        }
    }
}
