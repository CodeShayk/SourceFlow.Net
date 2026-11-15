using System.Globalization;
using System.Text;

namespace SourceFlow.Query;

/// <summary>A lexical token.</summary>
internal enum TokenKind
{
    EndOfInput,
    Identifier,
    QuotedIdentifier,
    String,
    Integer,
    Decimal,
    Duration,
    Parameter,
    Symbol,
    Comment,
}

/// <summary>A token: its kind, its text (the decoded value for strings and quoted identifiers) and its position.</summary>
internal readonly record struct Token(TokenKind Kind, string Text, TextSpan Span)
{
    public bool Is(string keyword)
    {
        return Kind == TokenKind.Identifier && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsSymbol(string symbol)
    {
        return Kind == TokenKind.Symbol && Text == symbol;
    }
}

/// <summary>
/// The request text guard (doc 14 §9.2): size, valid UTF-16 (no lone surrogates), NFC normalisation, and no NUL or
/// control characters (except TAB, LF, CR) or bidirectional overrides and isolates anywhere (SFQ0103/0104).
/// </summary>
internal static class RequestGuard
{
    public static string? Check(string text, QueryLimits limits, DiagnosticBag diagnostics)
    {
        if (text.Length > limits.MaxTextLength)
        {
            diagnostics.Add(QueryCodes.Limit, $"The query is longer than {limits.MaxTextLength} characters.", new TextSpan(0, 0));
            return null;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                diagnostics.Add(QueryCodes.ForbiddenCharacters, "The query is not valid Unicode text.", new TextSpan(i, 1));
                return null;
            }

            if (IsForbidden(c))
            {
                diagnostics.Add(QueryCodes.ForbiddenCharacters,
                    $"The query contains a forbidden character U+{(int)c:X4} (control, NUL or bidirectional formatting).", new TextSpan(i, 1));
                return null;
            }
        }

        return text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
    }

    public static bool IsZeroWidth(char c)
    {
        return c is '​' or '‌' or '‍' or '⁠' or '﻿';
    }

    private static bool IsForbidden(char c)
    {
        return (c < 0x20 && c is not ('\t' or '\n' or '\r'))
            || c is >= '\u007F' and <= '\u009F'
            || c is >= '‪' and <= '‮'
            || c is >= '⁦' and <= '⁩'
            || c is '‎' or '‏' or '؜';
    }
}

/// <summary>
/// The SFQL lexer. Identifiers are ASCII (no homoglyphs); zero-width characters are allowed only inside string
/// literals; comments and <c>;</c> are emitted as tokens so the parser can reject them by name (SFQ0102).
/// In SQL syntax <c>"…"</c> is a quoted identifier and <c>'…'</c> a string; in pipeline syntax both are strings.
/// A quote is escaped by doubling it.
/// </summary>
internal sealed class Lexer(string text, QueryLanguage language, QueryLimits limits, DiagnosticBag diagnostics)
{
    private static readonly string[] Symbols = ["!=", "<>", "<=", ">=", "--", "/*", "|", ",", "(", ")", ".", "=", "<", ">", "+", "-", "*", "/", "%", "[", "]", ";"];
    private int _position;

    public List<Token>? Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipWhitespace();
            if (diagnostics.HasErrors)
            {
                return null;
            }

            if (_position >= text.Length)
            {
                tokens.Add(new Token(TokenKind.EndOfInput, string.Empty, new TextSpan(text.Length, 0)));
                return tokens;
            }

            Token? token = Next();
            if (token is null)
            {
                return null;
            }

            tokens.Add(token.Value);
            if (token.Value.Kind == TokenKind.Comment)
            {
                // Nothing after a comment marker is lexed: the parser rejects the comment itself (SFQ0102).
                tokens.Add(new Token(TokenKind.EndOfInput, string.Empty, new TextSpan(text.Length, 0)));
                return tokens;
            }

            if (tokens.Count > limits.MaxTokens)
            {
                diagnostics.Add(QueryCodes.Limit, $"The query has more than {limits.MaxTokens} tokens.", token.Value.Span);
                return null;
            }
        }
    }

    private void SkipWhitespace()
    {
        while (_position < text.Length)
        {
            char c = text[_position];
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                _position++;
            }
            else if (RequestGuard.IsZeroWidth(c))
            {
                diagnostics.Add(QueryCodes.ForbiddenCharacters, $"Zero-width character U+{(int)c:X4} is only allowed inside string literals.", new TextSpan(_position, 1));
                return;
            }
            else
            {
                return;
            }
        }
    }

    private Token? Next()
    {
        int start = _position;
        char c = text[_position];
        if (IsIdentifierStart(c))
        {
            while (_position < text.Length && IsIdentifierPart(text[_position]))
            {
                _position++;
            }

            return new Token(TokenKind.Identifier, text[start.._position], new TextSpan(start, _position - start));
        }

        if (char.IsAsciiDigit(c))
        {
            return Number(start);
        }

        if (c == '@')
        {
            _position++;
            int nameStart = _position;
            while (_position < text.Length && IsIdentifierPart(text[_position]))
            {
                _position++;
            }

            if (_position == nameStart || !IsIdentifierStart(text[nameStart]))
            {
                diagnostics.Add(QueryCodes.Syntax, "A parameter is '@' followed by a name.", new TextSpan(start, _position - start + 1));
                return null;
            }

            return new Token(TokenKind.Parameter, text[nameStart.._position], new TextSpan(start, _position - start));
        }

        if (c == '\'' || (c == '"' && language == QueryLanguage.Sfql))
        {
            return Quoted(start, c, TokenKind.String);
        }

        if (c == '"')
        {
            return Quoted(start, c, TokenKind.QuotedIdentifier);
        }

        foreach (string symbol in Symbols)
        {
            if (string.CompareOrdinal(text, _position, symbol, 0, symbol.Length) == 0)
            {
                _position += symbol.Length;
                if (symbol is "--" or "/*")
                {
                    return new Token(TokenKind.Comment, symbol, new TextSpan(start, symbol.Length));
                }

                return new Token(TokenKind.Symbol, symbol, new TextSpan(start, symbol.Length));
            }
        }

        diagnostics.Add(c > 127 ? QueryCodes.ForbiddenCharacters : QueryCodes.Syntax,
            c > 127 ? $"Character U+{(int)c:X4} is only allowed inside string literals (identifiers are ASCII)." : $"Unexpected character {DiagnosticBag.Token(c.ToString())}.",
            new TextSpan(start, 1));
        return null;
    }

    private Token? Number(int start)
    {
        while (_position < text.Length && char.IsAsciiDigit(text[_position]))
        {
            _position++;
        }

        bool fraction = false;
        if (_position + 1 < text.Length && text[_position] == '.' && char.IsAsciiDigit(text[_position + 1]))
        {
            fraction = true;
            _position++;
            while (_position < text.Length && char.IsAsciiDigit(text[_position]))
            {
                _position++;
            }
        }

        string digits = text[start.._position];
        if (_position < text.Length && IsIdentifierStart(text[_position]))
        {
            int unitStart = _position;
            while (_position < text.Length && IsIdentifierPart(text[_position]))
            {
                _position++;
            }

            string unit = text[unitStart.._position];
            var span = new TextSpan(start, _position - start);
            if (unit == "m" && fraction)
            {
                return new Token(TokenKind.Decimal, digits, span);
            }

            if (!fraction && unit is "d" or "h" or "m" or "s" or "ms")
            {
                return new Token(TokenKind.Duration, digits + unit, span);
            }

            diagnostics.Add(QueryCodes.Syntax, "A number is followed by an unknown unit; durations use d, h, m, s or ms, decimals the suffix m.", span);
            return null;
        }

        return new Token(fraction ? TokenKind.Decimal : TokenKind.Integer, digits, new TextSpan(start, _position - start));
    }

    private Token? Quoted(int start, char quote, TokenKind kind)
    {
        _position++;
        var value = new StringBuilder();
        while (_position < text.Length)
        {
            char c = text[_position];
            if (c == quote)
            {
                if (_position + 1 < text.Length && text[_position + 1] == quote)
                {
                    value.Append(quote);
                    _position += 2;
                    continue;
                }

                _position++;
                return new Token(kind, value.ToString(), new TextSpan(start, _position - start));
            }

            value.Append(c);
            _position++;
        }

        diagnostics.Add(QueryCodes.Syntax, kind == TokenKind.String ? "A string is not closed." : "A quoted identifier is not closed.", new TextSpan(start, _position - start));
        return null;
    }

    private static bool IsIdentifierStart(char c)
    {
        return char.IsAsciiLetter(c) || c == '_';
    }

    private static bool IsIdentifierPart(char c)
    {
        return char.IsAsciiLetterOrDigit(c) || c == '_';
    }

    public static TimeSpan ParseDuration(string text)
    {
        int split = text.TakeWhile(char.IsAsciiDigit).Count();
        long amount = long.Parse(text.AsSpan(0, split), NumberStyles.None, CultureInfo.InvariantCulture);
        return text[split..] switch
        {
            "d" => TimeSpan.FromDays(amount),
            "h" => TimeSpan.FromHours(amount),
            "m" => TimeSpan.FromMinutes(amount),
            "s" => TimeSpan.FromSeconds(amount),
            _ => TimeSpan.FromMilliseconds(amount),
        };
    }
}
