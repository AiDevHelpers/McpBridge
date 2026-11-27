namespace AiDevHelpers.Helpers.WindowsSession
{
    /// <summary>Builds the command line for a process started in a user session.</summary>
    public static class UserProcessCommandLine
    {
        public static string Build(string fileName, string arguments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            if (fileName.Contains('"'))
            {
                throw new ArgumentException(Strings.Get("FileNameContainsQuote"), nameof(fileName));
            }

            return string.IsNullOrEmpty(arguments) ? $"\"{fileName}\"" : $"\"{fileName}\" {arguments}";
        }
    }
}
