using System;
using System.Collections.Generic;
using System.Linq;

namespace Keepsake.Cli
{
    public static class ExitCodes
    {
        public const int Success = 0;
        public const int Warning = 1;
        public const int Failed = 2;
        public const int Usage = 3;
        public const int WrongCredential = 4;
        public const int Locked = 5;
        public const int Cancelled = 6;
    }

    public sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    /// <summary>Parsed arguments: <c>--name value</c> options, <c>--flag</c> switches and positionals.</summary>
    public sealed class ParsedArgs
    {
        private readonly Dictionary<string, List<string>> _values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<string> Positionals { get; } = new List<string>();

        public static ParsedArgs Parse(IList<string> args, ICollection<string> valueOptions, ICollection<string> flagOptions)
        {
            var result = new ParsedArgs();
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                if (a == "--")
                {
                    result.Positionals.AddRange(args.Skip(i + 1));
                    break;
                }
                if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
                {
                    string name = a.Substring(2);
                    string inline = null;
                    int eq = name.IndexOf('=');
                    if (eq > 0)
                    {
                        inline = name.Substring(eq + 1);
                        name = name.Substring(0, eq);
                    }
                    if (valueOptions.Contains(name))
                    {
                        string value = inline;
                        if (value == null)
                        {
                            if (i + 1 >= args.Count)
                                throw new UsageException("Option --" + name + " needs a value.");
                            value = args[++i];
                        }
                        if (!result._values.TryGetValue(name, out var list))
                            result._values[name] = list = new List<string>();
                        list.Add(value);
                    }
                    else if (flagOptions.Contains(name))
                    {
                        if (inline != null)
                            throw new UsageException("Option --" + name + " does not take a value.");
                        result._flags.Add(name);
                    }
                    else
                    {
                        throw new UsageException("Unknown option --" + name + ".");
                    }
                }
                else
                {
                    result.Positionals.Add(a);
                }
            }
            return result;
        }

        public string Value(string name)
        {
            if (!_values.TryGetValue(name, out var list))
                return null;
            if (list.Count > 1)
                throw new UsageException("Option --" + name + " may only be given once.");
            return list[0];
        }

        public List<string> Values(string name) => _values.TryGetValue(name, out var list) ? list : new List<string>();

        public bool Flag(string name) => _flags.Contains(name);

        public int IntValue(string name, int fallback, int min, int max)
        {
            string v = Value(name);
            if (v == null)
                return fallback;
            if (!int.TryParse(v, out int n) || n < min || n > max)
                throw new UsageException("Option --" + name + " must be a number between " + min + " and " + max + ".");
            return n;
        }
    }
}
