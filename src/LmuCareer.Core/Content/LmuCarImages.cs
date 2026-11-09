using System.IO.Compression;
using System.Text.RegularExpressions;

namespace LmuCareer.Core.Content;

/// <summary>
/// The pictures of each car entry that LMU's own menus show, from the menus' bundle (Bin\UI.zip):
/// start/images/cars/&lt;vehicle id&gt;_frontAngle.webp. Read only, like every LMU file the app reads.
/// </summary>
public static partial class LmuCarImages
{
    /// <summary>
    /// The entry's front-angle picture as WebP bytes, the menus' thumbnail when there is one (about
    /// 45 KB, against 150 KB); null when there's none or the bundle can't be read.
    /// </summary>
    public static byte[]? FrontAngle(string lmuRoot, string vehicleId)
    {
        if (!VehicleId().IsMatch(vehicleId)) return null;
        var bundle = Path.Combine(lmuRoot, "Bin", "UI.zip");
        try
        {
            using var zip = ZipFile.OpenRead(bundle);
            // The bundle names its entries with backslashes (start\images\cars\...).
            var entry = new[] { $"FrontThumbnail\\{vehicleId}_frontAngle.webp", $"{vehicleId}_frontAngle.webp" }
                .SelectMany(name => new[] { $"start\\images\\cars\\{name}", $"start/images/cars/{name.Replace('\\', '/')}" })
                .Select(zip.GetEntry)
                .FirstOrDefault(e => e is not null);
            if (entry is null) return null;
            using var stream = entry.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // LMU's ids are letters, digits and underscores (21_26_AFCO31910023): nothing that could reach another file.
    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex VehicleId();
}
