namespace UAssetAPI
{
    /// <summary>
    /// Provides the raw files of packages by their asset path, e.g. from a game's .pak/IoStore archives.
    /// </summary>
    public interface IAssetResolver
    {
        /// <summary>
        /// Looks up a package.
        /// </summary>
        /// <param name="packagePath">The package path, e.g. "/Game/UI/WBP_Menu" (without an object name or extension).</param>
        /// <param name="uasset">The .uasset (or .umap) file in the legacy package format.</param>
        /// <param name="uexp">The .uexp file, or null when the package has none.</param>
        /// <returns>Whether the package was found.</returns>
        bool TryResolve(string packagePath, out byte[] uasset, out byte[] uexp);
    }
}
