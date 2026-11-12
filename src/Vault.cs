using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OnScreenKeyboard;

public class VaultItem
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Secret values (passwords) are masked in the UI and only matched by name.</summary>
    public bool Secret { get; set; }
    public bool Favorite { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string FavoriteLabel => Favorite ? "★" : "☆";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => Secret ? "•••••••• (secret)" : Value;
}

/// <summary>Saved keywords, emails and passwords, encrypted at rest with the current Windows user's DPAPI key.</summary>
public class Vault
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnScreenKeyboard", "vault.dat");

    public List<VaultItem> Items { get; private set; } = [];

    public event Action? Changed;

    public static Vault Load()
    {
        var vault = new Vault();
        try
        {
            if (File.Exists(FilePath))
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                vault.Items = JsonSerializer.Deserialize<List<VaultItem>>(json) ?? [];
            }
        }
        catch { /* unreadable vault (other user/machine or corrupt): start empty, don't overwrite until saved */ }
        return vault;
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(Items);
            File.WriteAllBytes(FilePath, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }
        catch { return false; }
        Changed?.Invoke();
        return true;
    }

    public bool Add(VaultItem item)
    {
        Items.Add(item);
        if (Save()) return true;
        Items.Remove(item);
        return false;
    }

    public bool Remove(VaultItem item)
    {
        int index = Items.IndexOf(item);
        if (index < 0) return false;
        Items.RemoveAt(index);
        if (Save()) return true;
        Items.Insert(index, item);
        return false;
    }

    public List<VaultItem> Search(string query) => Items
        .Where(i => i.Name.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)
            || (!i.Secret && i.Value.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)))
        .OrderByDescending(i => i.Favorite)
        .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool ToggleFavorite(VaultItem item)
    {
        item.Favorite = !item.Favorite;
        if (Save()) return true;
        item.Favorite = !item.Favorite;
        return false;
    }

    /// <summary>Items whose name (or non-secret value) starts with what has been typed so far.</summary>
    public List<VaultItem> Match(string typed)
    {
        if (typed.Length == 0) return [];
        return Items
            .Where(i => i.Name.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase)
                        || (!i.Secret && i.Value.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase)))
            .Where(i => i.Secret || !string.Equals(i.Value, typed, StringComparison.CurrentCulture))
            .Take(6)
            .ToList();
    }
}
