namespace ParallelPassagesEditor.Services
{
    /// <summary>
    /// Stands in for the editor's Loc (WPF resources) in the linked model code. The plugin
    /// never shows the model's display texts, so the key itself is enough.
    /// </summary>
    internal static class Loc
    {
        public static string Get(string key)
        {
            return key;
        }
    }
}
