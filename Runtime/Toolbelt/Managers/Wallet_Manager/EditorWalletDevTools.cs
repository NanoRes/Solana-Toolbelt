using System;
#if UNITY_EDITOR
using System.Globalization;
using System.Text;
using UnityEditor;
#endif
using UnityEngine;

namespace Solana.Unity.Toolbelt
{
    /// <summary>
    /// Utility for storing and retrieving a developer private key for editor testing.
    /// Uses EditorPrefs so the key is kept local and out of builds.
    /// </summary>
    public static class EditorWalletDevTools
    {
#if UNITY_EDITOR

        private const string PrivateKeyPref = "SolanaDevPrivateKey";

        public static string PrivateKey
        {
            get => EditorPrefs.GetString(PrivateKeyPref, string.Empty);
            set => EditorPrefs.SetString(PrivateKeyPref, value ?? string.Empty);
        }

        /// <summary>
        /// Attempts to normalise user provided private key text into a form
        /// that can be consumed by the runtime wallet import helpers.
        /// </summary>
        /// <param name="input">Raw text pasted into the editor window.</param>
        /// <param name="normalized">Sanitised string when successful.</param>
        /// <param name="error">Human readable error when parsing fails.</param>
        /// <returns>True when a usable private key could be produced.</returns>
        public static bool TryNormalizePrivateKey(string input, out string normalized, out string error)
        {
            normalized = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(input))
            {
                return true;
            }

            string trimmed = input.Trim();

            // Strip wrapping quotes that may appear when copying JSON values.
            if (trimmed.Length >= 2 &&
                ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
            }

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                if (!TryExtractJsonSecret(trimmed, out string extracted))
                {
                    error = "Unable to locate a secret or private key entry in the provided JSON.";
                    return false;
                }

                trimmed = extracted.Trim();
            }

            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                return TryNormalizeByteArray(trimmed, out normalized, out error);
            }

            normalized = trimmed;
            return true;
        }

        private static bool TryExtractJsonSecret(string json, out string value)
        {
            string[] keys =
            {
                "\"secret_key\"",
                "\"secretKey\"",
                "\"private_key\"",
                "\"privateKey\"",
                "\"secret\"",
                "\"private\""
            };

            foreach (string key in keys)
            {
                int keyIndex = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (keyIndex < 0)
                {
                    continue;
                }

                int colonIndex = json.IndexOf(':', keyIndex + key.Length);
                if (colonIndex < 0)
                {
                    continue;
                }

                int valueStart = colonIndex + 1;
                while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                {
                    valueStart++;
                }

                if (valueStart >= json.Length)
                {
                    break;
                }

                char lead = json[valueStart];
                if (lead == '[')
                {
                    int depth = 0;
                    for (int i = valueStart; i < json.Length; i++)
                    {
                        char c = json[i];
                        if (c == '[')
                        {
                            depth++;
                        }
                        else if (c == ']')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                value = json.Substring(valueStart, i - valueStart + 1);
                                return true;
                            }
                        }
                    }
                }
                else if (lead == '"' || lead == '\'')
                {
                    char quote = lead;
                    var builder = new StringBuilder();
                    bool escaping = false;
                    for (int i = valueStart + 1; i < json.Length; i++)
                    {
                        char c = json[i];
                        if (escaping)
                        {
                            builder.Append(c);
                            escaping = false;
                            continue;
                        }

                        if (c == '\\')
                        {
                            escaping = true;
                            continue;
                        }

                        if (c == quote)
                        {
                            value = builder.ToString();
                            return true;
                        }

                        builder.Append(c);
                    }
                }
                else
                {
                    int end = valueStart;
                    while (end < json.Length && !char.IsWhiteSpace(json[end]) && json[end] != ',' && json[end] != '}')
                    {
                        end++;
                    }

                    if (end > valueStart)
                    {
                        value = json.Substring(valueStart, end - valueStart);
                        return true;
                    }
                }
            }

            value = null;
            return false;
        }

        private static bool TryNormalizeByteArray(string input, out string normalized, out string error)
        {
            string inner = input.Trim();
            if (!inner.StartsWith("[", StringComparison.Ordinal) || !inner.EndsWith("]", StringComparison.Ordinal))
            {
                normalized = string.Empty;
                error = "Byte array secrets must be wrapped in square brackets.";
                return false;
            }

            inner = inner.Trim('[', ']');

            string[] tokens = inner.Split(new[] { ',', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
            {
                normalized = "[]";
                error = null;
                return true;
            }

            var builder = new StringBuilder();
            builder.Append('[');

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (!byte.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    normalized = string.Empty;
                    error = $"`{token}` is not a valid byte value.";
                    return false;
                }

                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(token);
            }

            builder.Append(']');
            normalized = builder.ToString();
            error = null;
            return true;
        }
#else
        public static string PrivateKey
        {
            get => string.Empty;
            set { }
        }
#endif

        public static bool HasPrivateKey
        {
            get
            {
#if UNITY_EDITOR
                return !string.IsNullOrEmpty(PrivateKey);
#else
                return false;
#endif
            }
        }
    }
}
