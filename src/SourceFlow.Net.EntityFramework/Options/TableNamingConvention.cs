#nullable enable

using System;
using System.Text.RegularExpressions;

namespace SourceFlow.Stores.EntityFramework.Options
{
    /// <summary>
    /// Defines table naming conventions for Entity Framework stores.
    /// </summary>
    public class TableNamingConvention
    {
        /// <summary>
        /// Gets or sets the casing style for table names.
        /// </summary>
        public TableNameCasing Casing { get; set; } = TableNameCasing.PascalCase;

        /// <summary>
        /// Gets or sets whether to pluralize table names.
        /// </summary>
        public bool Pluralize { get; set; } = false;

        /// <summary>
        /// Gets or sets an optional prefix for all table names.
        /// </summary>
        public string? Prefix { get; set; }

        /// <summary>
        /// Gets or sets an optional suffix for all table names.
        /// </summary>
        public string? Suffix { get; set; }

        /// <summary>
        /// Gets or sets whether to use schema names.
        /// </summary>
        public bool UseSchema { get; set; } = false;

        /// <summary>
        /// Gets or sets the schema name to use (if UseSchema is true).
        /// </summary>
        public string? SchemaName { get; set; }

        /// <summary>
        /// Applies the naming convention to a type name.
        /// </summary>
        /// <param name="typeName">The type name to convert</param>
        /// <returns>The table name following the convention</returns>
        public string ApplyConvention(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return typeName;

            var tableName = typeName;

            // On casing
            tableName = Casing switch
            {
                TableNameCasing.PascalCase => ToPascalCase(tableName),
                TableNameCasing.CamelCase => ToCamelCase(tableName),
                TableNameCasing.SnakeCase => ToSnakeCase(tableName),
                TableNameCasing.LowerCase => tableName.ToLowerInvariant(),
                TableNameCasing.UpperCase => tableName.ToUpperInvariant(),
                _ => tableName
            };

            // On pluralization
            if (Pluralize)
            {
                tableName = PluralizeName(tableName);
            }

            // On prefix and suffix
            if (!string.IsNullOrEmpty(Prefix))
            {
                tableName = Prefix + tableName;
            }

            if (!string.IsNullOrEmpty(Suffix))
            {
                tableName = tableName + Suffix;
            }

            return tableName;
        }

        private static string ToPascalCase(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Already in PascalCase, return as is
            return input;
        }

        private static string ToCamelCase(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return char.ToLowerInvariant(input[0]) + input.Substring(1);
        }

        private static string ToSnakeCase(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Insert underscores before capital letters (except the first one)
            var result = Regex.Replace(input, "(?<!^)([A-Z])", "_$1");
            return result.ToLowerInvariant();
        }

        private static string PluralizeName(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Simple pluralization rules
            if (input.EndsWith("y", StringComparison.OrdinalIgnoreCase) &&
                input.Length > 1 &&
                !IsVowel(input[input.Length - 2]))
            {
                // party -> parties
                return input.Substring(0, input.Length - 1) + "ies";
            }
            else if (input.EndsWith("s", StringComparison.OrdinalIgnoreCase) ||
                input.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
                input.EndsWith("z", StringComparison.OrdinalIgnoreCase) ||
                input.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
                input.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
            {
                // class -> classes, box -> boxes
                return input + "es";
            }
            else
            {
                // Default: just add 's'
                return input + "s";
            }
        }

        private static bool IsVowel(char c)
        {
            return "aeiouAEIOU".IndexOf(c) >= 0;
        }
    }

    /// <summary>
    /// Defines the casing styles available for table names.
    /// </summary>
    public enum TableNameCasing
    {
        /// <summary>
        /// PascalCase - first letter capitalized (e.g., BankAccount)
        /// </summary>
        PascalCase,

        /// <summary>
        /// camelCase - first letter lowercase (e.g., bankAccount)
        /// </summary>
        CamelCase,

        /// <summary>
        /// snake_case - lowercase with underscores (e.g., bank_account)
        /// </summary>
        SnakeCase,

        /// <summary>
        /// lowercase - all lowercase (e.g., bankaccount)
        /// </summary>
        LowerCase,

        /// <summary>
        /// UPPERCASE - all uppercase (e.g., BANKACCOUNT)
        /// </summary>
        UpperCase
    }
}
