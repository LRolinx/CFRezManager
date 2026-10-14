using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CFRezManager;

internal sealed record CrossFireDatDocument(
    int Version,
    string ObjectKind,
    int ObjectCount,
    IReadOnlyList<CrossFireDatZone> Zones,
    IReadOnlyList<CrossFireDatEnvSound> EnvSounds,
    CrossFireDatMovePath? MovePath,
    CrossFireDatCamera? Camera,
    string Text,
    string StorageDescription,
    int SourceByteCount,
    int DecodedByteCount);

internal sealed record CrossFireDatZone(
    string Name,
    CrossFireDatVector3? Position,
    CrossFireDatVector3? Rotation,
    int? Floor,
    string SoundType,
    IReadOnlyList<string> EnvFModEvents,
    IReadOnlyList<CrossFireDatVector3> Points);

internal sealed record CrossFireDatEnvSound(
    string Name,
    string ZoneName,
    CrossFireDatVector3? Position,
    IReadOnlyList<string> SoundNames,
    double? Decay,
    double? RadiusIn,
    double? RadiusOut,
    string SoundType);

internal sealed record CrossFireDatMovePath(
    double? FirstFrame,
    double? LastFrame,
    double? FrameSpeed,
    int? AnimationFrameCount,
    IReadOnlyList<CrossFireDatPathSample> Samples);

internal sealed record CrossFireDatCamera(
    double? FirstFrame,
    double? LastFrame,
    double? FrameSpeed,
    IReadOnlyList<CrossFireDatCameraTrack> Tracks);

internal sealed record CrossFireDatCameraTrack(
    string Name,
    CrossFireDatVector3? LocalPosition,
    int? AnimationFrameCount,
    IReadOnlyList<CrossFireDatPathSample> Samples);

internal sealed record CrossFireDatPathSample(double Frame, CrossFireDatVector3 Position);

internal readonly record struct CrossFireDatVector3(double X, double Y, double Z);

internal static class CrossFireDatDecoder
{
    private const int HeaderLength = sizeof(int);
    private const int MaxVersion = 1024;
    private const string BaseStorageDescription = "CrossFire DAT";
    private const string NumberPattern = @"-?\d+(?:\.\d+)?";

    private static readonly Regex ZoneStartRegex = new(@"\(\s*Zoneman(?!Name)", RegexOptions.CultureInvariant);
    private static readonly Regex EnvSoundStartRegex = new(@"\(\s*EnvSound(?!Name)", RegexOptions.CultureInvariant);
    private static readonly Regex CameraTrackStartRegex = new(@"\*CAMERA_(FOV|LOOK|EYE)_ANIMATION", RegexOptions.CultureInvariant);
    private static readonly Regex PathSampleRegex = new(
        $@"(?<![\d.-])({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})",
        RegexOptions.CultureInvariant);

    public static bool IsCandidate(string extension)
    {
        return string.Equals(extension, "dat", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryDecode(
        byte[] data,
        string fallbackName,
        out CrossFireDatDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
        if (prepared is null)
        {
            errorMessage = "DAT compression could not be decoded.";
            return false;
        }

        if (prepared.Length <= HeaderLength)
        {
            errorMessage = "DAT file is too small.";
            return false;
        }

        byte[] versionBytes = prepared.AsSpan(0, HeaderLength).ToArray();
        RezCrypto.Decode(versionBytes, 0);
        int version = BinaryPrimitives.ReadInt32LittleEndian(versionBytes);
        if (version <= 0 || version > MaxVersion)
        {
            errorMessage = "DAT header is not a recognized CrossFire object file.";
            return false;
        }

        byte[] decodedBody = prepared.AsSpan(HeaderLength).ToArray();
        // CrossFire stores this DAT as two independently encoded chunks: version header, then body.
        RezCrypto.Decode(decodedBody, 0);
        string rawText = Encoding.Latin1.GetString(decodedBody);

        List<CrossFireDatZone> zones = ParseZones(rawText);
        if (zones.Count > 0)
        {
            string storageDescription = FormatStorageDescription(prepared, data, "Zoneman");
            string text = FormatZoneText(fallbackName, version, data.Length, prepared.Length, zones);
            document = new CrossFireDatDocument(
                version,
                "Zoneman",
                zones.Count,
                zones,
                [],
                null,
                null,
                text,
                storageDescription,
                data.Length,
                prepared.Length);
            return true;
        }

        List<CrossFireDatEnvSound> envSounds = ParseEnvSounds(rawText);
        if (envSounds.Count > 0)
        {
            string storageDescription = FormatStorageDescription(prepared, data, "EnvSound");
            string text = FormatEnvSoundText(fallbackName, version, data.Length, prepared.Length, envSounds);
            document = new CrossFireDatDocument(
                version,
                "EnvSound",
                envSounds.Count,
                [],
                envSounds,
                null,
                null,
                text,
                storageDescription,
                data.Length,
                prepared.Length);
            return true;
        }

        CrossFireDatMovePath? movePath = ParseMovePath(rawText);
        if (movePath is not null)
        {
            string storageDescription = FormatStorageDescription(prepared, data, "MovePath");
            string text = FormatMovePathText(fallbackName, version, data.Length, prepared.Length, movePath);
            document = new CrossFireDatDocument(
                version,
                "MovePath",
                1,
                [],
                [],
                movePath,
                null,
                text,
                storageDescription,
                data.Length,
                prepared.Length);
            return true;
        }

        CrossFireDatCamera? camera = ParseCamera(rawText);
        if (camera is not null)
        {
            string storageDescription = FormatStorageDescription(prepared, data, "CameraAnimation");
            string text = FormatCameraText(fallbackName, version, data.Length, prepared.Length, camera);
            document = new CrossFireDatDocument(
                version,
                "CameraAnimation",
                camera.Tracks.Count,
                [],
                [],
                null,
                camera,
                text,
                storageDescription,
                data.Length,
                prepared.Length);
            return true;
        }

        // The shared sound-filter resource is a legacy LithTech attributes
        // file rather than one of CrossFire's zone/path object blocks. Its
        // version header and body still use the CrossFire REZ transform, so
        // expose the decrypted body losslessly instead of reporting it as an
        // undecodable DAT.
        if (rawText.Contains("No One Lives Forever Sound Filters Attributes file", StringComparison.OrdinalIgnoreCase))
        {
            string storageDescription = FormatStorageDescription(prepared, data, "SoundFilterAttributes");
            string text = FormatEscapedText(fallbackName, version, data.Length, prepared.Length, decodedBody);
            document = new CrossFireDatDocument(
                version,
                "SoundFilterAttributes",
                1,
                [],
                [],
                null,
                null,
                text,
                storageDescription,
                data.Length,
                prepared.Length);
            return true;
        }

        errorMessage = "DAT body did not contain recognizable CrossFire objects.";
        return false;
    }

    private static string FormatEscapedText(
        string fileName,
        int version,
        int sourceByteCount,
        int decodedByteCount,
        ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length + 256);
        builder.AppendLine($"CrossFire DAT sound-filter attributes: {fileName}");
        builder.AppendLine($"Version: {version}");
        builder.AppendLine($"Source/decoded bytes: {sourceByteCount:N0} / {decodedByteCount:N0}");
        builder.AppendLine("Non-ASCII/control bytes are retained as <0xNN>.");
        builder.AppendLine();

        foreach (byte value in bytes)
        {
            if (value is (byte)'\r' or (byte)'\n' or (byte)'\t' || value is >= 0x20 and <= 0x7E)
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append("<0x");
                builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
                builder.Append('>');
            }
        }

        return builder.ToString();
    }

    private static List<CrossFireDatZone> ParseZones(string rawText)
    {
        MatchCollection matches = ZoneStartRegex.Matches(rawText);
        var zones = new List<CrossFireDatZone>(matches.Count);

        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : rawText.Length;
            if (end <= start)
            {
                continue;
            }

            string chunk = rawText[start..end];
            string name = ReadStringProperty(chunk, "ZonemanName") ?? "Zoneman";
            CrossFireDatVector3? position = ReadVectorProperty(chunk, "Pos");
            CrossFireDatVector3? rotation = ReadVectorProperty(chunk, "Rot");
            int? floor = ReadOptionalIntProperty(chunk, "Floor");
            string soundType = ReadStringProperty(chunk, "SoundType") ?? string.Empty;
            IReadOnlyList<string> envFModEvents = new[]
                {
                    ReadStringProperty(chunk, "EnvFModEvent1"),
                    ReadStringProperty(chunk, "EnvFModEvent2"),
                    ReadStringProperty(chunk, "EnvFModEvent3")
                }
                .Where(value => value is not null)
                .Select(value => value!)
                .ToArray();
            IReadOnlyList<CrossFireDatVector3> points = ReadVectorProperties(chunk, "Point");

            if (position is null && points.Count == 0 && string.Equals(name, "Zoneman", StringComparison.Ordinal))
            {
                continue;
            }

            zones.Add(new CrossFireDatZone(name, position, rotation, floor, soundType, envFModEvents, points));
        }

        return zones;
    }

    private static List<CrossFireDatEnvSound> ParseEnvSounds(string rawText)
    {
        MatchCollection matches = EnvSoundStartRegex.Matches(rawText);
        var envSounds = new List<CrossFireDatEnvSound>(matches.Count);

        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : rawText.Length;
            if (end <= start)
            {
                continue;
            }

            string chunk = rawText[start..end];
            string name = ReadStringProperty(chunk, "EnvSoundName") ?? "EnvSound";
            string zoneName = ReadStringProperty(chunk, "ZoneName") ?? string.Empty;
            CrossFireDatVector3? position = ReadVectorProperty(chunk, "Pos");
            string[] soundNames =
            [
                ReadLooseTextProperty(chunk, "SoundName1") ?? string.Empty,
                ReadLooseTextProperty(chunk, "SoundName2") ?? string.Empty,
                ReadLooseTextProperty(chunk, "SoundName3") ?? string.Empty
            ];
            double? decay = ReadOptionalDoubleProperty(chunk, "Decay");
            double? radiusIn = ReadOptionalDoubleProperty(chunk, "Radius_In");
            double? radiusOut = ReadOptionalDoubleProperty(chunk, "Radius_Out");
            string soundType = ReadStringProperty(chunk, "soundType") ?? ReadStringProperty(chunk, "SoundType") ?? string.Empty;

            if (position is null && soundNames.All(string.IsNullOrEmpty) && string.Equals(name, "EnvSound", StringComparison.Ordinal))
            {
                continue;
            }

            envSounds.Add(new CrossFireDatEnvSound(name, zoneName, position, soundNames, decay, radiusIn, radiusOut, soundType));
        }

        return envSounds;
    }

    private static CrossFireDatMovePath? ParseMovePath(string rawText)
    {
        if (!rawText.Contains("*MOVE_PATH", StringComparison.Ordinal))
        {
            return null;
        }

        double? firstFrame = ReadStarDouble(rawText, "SCENE_FIRSTFRAME");
        double? lastFrame = ReadStarDouble(rawText, "SCENE_LASTFRAME");
        double? frameSpeed = ReadStarDouble(rawText, "SCENE_FRAMESPEED");
        int? animationFrameCount = ReadStarInt(rawText, "ANIMATION_FRAME_COUNT");
        IReadOnlyList<CrossFireDatPathSample> samples = ParsePathSamples(rawText);

        return firstFrame is null &&
               lastFrame is null &&
               frameSpeed is null &&
               animationFrameCount is null &&
               samples.Count == 0
            ? null
            : new CrossFireDatMovePath(firstFrame, lastFrame, frameSpeed, animationFrameCount, samples);
    }

    private static CrossFireDatCamera? ParseCamera(string rawText)
    {
        if (!rawText.Contains("*CAMERA_", StringComparison.Ordinal))
        {
            return null;
        }

        MatchCollection matches = CameraTrackStartRegex.Matches(rawText);
        if (matches.Count == 0)
        {
            return null;
        }

        double? firstFrame = ReadStarDouble(rawText, "SCENE_FIRSTFRAME");
        double? lastFrame = ReadStarDouble(rawText, "SCENE_LASTFRAME");
        double? frameSpeed = ReadStarDouble(rawText, "SCENE_FRAMESPEED");
        var tracks = new List<CrossFireDatCameraTrack>(matches.Count);

        for (int i = 0; i < matches.Count; i++)
        {
            Match match = matches[i];
            int start = match.Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : rawText.Length;
            if (end <= start)
            {
                continue;
            }

            string chunk = rawText[start..end];
            string name = match.Groups[1].Value;
            CrossFireDatVector3? localPosition = ReadStarVector(chunk, "LOCAL_POS");
            int? animationFrameCount = ReadStarInt(chunk, "ANIMATION_FRAME_COUNT");
            int sampleStart = chunk.IndexOf("*CONTROL_POS_SAMPLE", StringComparison.Ordinal);
            IReadOnlyList<CrossFireDatPathSample> samples = sampleStart >= 0
                ? ParsePathSamples(chunk[sampleStart..])
                : [];

            tracks.Add(new CrossFireDatCameraTrack(name, localPosition, animationFrameCount, samples));
        }

        return tracks.Count == 0 ? null : new CrossFireDatCamera(firstFrame, lastFrame, frameSpeed, tracks);
    }

    private static IReadOnlyList<CrossFireDatPathSample> ParsePathSamples(string text)
    {
        var samples = new List<CrossFireDatPathSample>();

        foreach (Match match in PathSampleRegex.Matches(text))
        {
            double frame = ReadDouble(match.Groups[1].Value);
            var position = new CrossFireDatVector3(
                ReadDouble(match.Groups[2].Value),
                ReadDouble(match.Groups[3].Value),
                ReadDouble(match.Groups[4].Value));
            samples.Add(new CrossFireDatPathSample(frame, position));
        }

        return samples;
    }

    private static string? ReadStringProperty(string chunk, string propertyName)
    {
        Match match = Regex.Match(
            chunk,
            $@"\(\s*{Regex.Escape(propertyName)}\s+""([^""]*)""\s*\)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ReadLooseTextProperty(string chunk, string propertyName)
    {
        string? quoted = ReadStringProperty(chunk, propertyName);
        if (quoted is not null)
        {
            return quoted;
        }

        Match match = Regex.Match(
            chunk,
            $@"\(\s*{Regex.Escape(propertyName)}\s+([^)]*?)\s*\)",
            RegexOptions.CultureInvariant);
        return match.Success ? CleanLooseText(match.Groups[1].Value) : null;
    }

    private static int? ReadOptionalIntProperty(string chunk, string propertyName)
    {
        Match match = Regex.Match(
            chunk,
            $@"\(\s*{Regex.Escape(propertyName)}\s+(-?\d*)\s*\)",
            RegexOptions.CultureInvariant);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    private static double? ReadOptionalDoubleProperty(string chunk, string propertyName)
    {
        Match match = Regex.Match(
            chunk,
            $@"\(\s*{Regex.Escape(propertyName)}\s+(-?\d+(?:\.\d+)?)\s*\)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private static double? ReadStarDouble(string text, string propertyName)
    {
        Match match = Regex.Match(
            text,
            $@"\*{Regex.Escape(propertyName)}\s+(-?\d+(?:\.\d+)?)",
            RegexOptions.CultureInvariant);
        return match.Success ? ReadDouble(match.Groups[1].Value) : null;
    }

    private static int? ReadStarInt(string text, string propertyName)
    {
        Match match = Regex.Match(
            text,
            $@"\*{Regex.Escape(propertyName)}\s+(-?\d+)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    private static CrossFireDatVector3? ReadStarVector(string text, string propertyName)
    {
        Match match = Regex.Match(
            text,
            $@"\*{Regex.Escape(propertyName)}\s+({NumberPattern})\s+({NumberPattern})\s+({NumberPattern})",
            RegexOptions.CultureInvariant);
        return match.Success ? ReadVector(match) : null;
    }

    private static CrossFireDatVector3? ReadVectorProperty(string chunk, string propertyName)
    {
        Match match = CreateVectorRegex(propertyName).Match(chunk);
        return match.Success ? ReadVector(match) : null;
    }

    private static IReadOnlyList<CrossFireDatVector3> ReadVectorProperties(string chunk, string propertyName)
    {
        MatchCollection matches = CreateVectorRegex(propertyName).Matches(chunk);
        var vectors = new List<CrossFireDatVector3>(matches.Count);
        foreach (Match match in matches)
        {
            vectors.Add(ReadVector(match));
        }

        return vectors;
    }

    private static Regex CreateVectorRegex(string propertyName)
    {
        const string number = @"(-?\d+(?:\.\d+)?)";
        return new Regex(
            $@"\(\s*{Regex.Escape(propertyName)}\s+{number}\s+{number}\s+{number}\s*\)",
            RegexOptions.CultureInvariant);
    }

    private static CrossFireDatVector3 ReadVector(Match match)
    {
        return new CrossFireDatVector3(
            ReadDouble(match.Groups[1].Value),
            ReadDouble(match.Groups[2].Value),
            ReadDouble(match.Groups[3].Value));
    }

    private static double ReadDouble(string value)
    {
        return double.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string FormatStorageDescription(byte[] prepared, byte[] source, string objectKind)
    {
        return ReferenceEquals(prepared, source)
            ? $"{BaseStorageDescription} / {objectKind}"
            : $"LZMA-compressed {BaseStorageDescription} / {objectKind}";
    }

    private static string FormatZoneText(
        string fallbackName,
        int version,
        int sourceByteCount,
        int decodedByteCount,
        IReadOnlyList<CrossFireDatZone> zones)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"; decoded from {fallbackName}");
        builder.AppendLine($"; version {version}, zones {zones.Count}, bytes {sourceByteCount} -> {decodedByteCount}");
        builder.AppendLine();

        foreach (CrossFireDatZone zone in zones)
        {
            builder.AppendLine("( Zoneman");
            builder.AppendLine($"\t( ZonemanName \"{Escape(zone.Name)}\" )");
            if (zone.Position is { } position)
            {
                builder.AppendLine($"\t( Pos {FormatVector(position)} )");
            }

            if (zone.Rotation is { } rotation)
            {
                builder.AppendLine($"\t( Rot {FormatVector(rotation)} )");
            }

            builder.AppendLine($"\t( Floor {zone.Floor?.ToString(CultureInfo.InvariantCulture) ?? string.Empty} )");
            if (!string.IsNullOrEmpty(zone.SoundType))
            {
                builder.AppendLine($"\t( SoundType \"{Escape(zone.SoundType)}\" )");
            }

            for (int i = 0; i < zone.EnvFModEvents.Count; i++)
            {
                builder.AppendLine($"\t( EnvFModEvent{i + 1} \"{Escape(zone.EnvFModEvents[i])}\" )");
            }

            foreach (CrossFireDatVector3 point in zone.Points)
            {
                builder.AppendLine($"\t( Point {FormatVector(point)} )");
            }

            builder.AppendLine(")");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatEnvSoundText(
        string fallbackName,
        int version,
        int sourceByteCount,
        int decodedByteCount,
        IReadOnlyList<CrossFireDatEnvSound> envSounds)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"; decoded from {fallbackName}");
        builder.AppendLine($"; version {version}, env sounds {envSounds.Count}, bytes {sourceByteCount} -> {decodedByteCount}");
        builder.AppendLine();

        foreach (CrossFireDatEnvSound envSound in envSounds)
        {
            builder.AppendLine("( EnvSound");
            builder.AppendLine($"\t( EnvSoundName \"{Escape(envSound.Name)}\" )");
            if (!string.IsNullOrEmpty(envSound.ZoneName))
            {
                builder.AppendLine($"\t( ZoneName \"{Escape(envSound.ZoneName)}\" )");
            }

            if (envSound.Position is { } position)
            {
                builder.AppendLine($"\t( Pos {FormatVector(position)} )");
            }

            for (int i = 0; i < envSound.SoundNames.Count; i++)
            {
                builder.AppendLine($"\t( SoundName{i + 1} \"{Escape(envSound.SoundNames[i])}\" )");
            }

            if (envSound.Decay is { } decay)
            {
                builder.AppendLine($"\t( Decay {FormatNumber(decay)} )");
            }

            if (envSound.RadiusIn is { } radiusIn)
            {
                builder.AppendLine($"\t( Radius_In {FormatNumber(radiusIn)} )");
            }

            if (envSound.RadiusOut is { } radiusOut)
            {
                builder.AppendLine($"\t( Radius_Out {FormatNumber(radiusOut)} )");
            }

            if (!string.IsNullOrEmpty(envSound.SoundType))
            {
                builder.AppendLine($"\t( soundType \"{Escape(envSound.SoundType)}\" )");
            }

            builder.AppendLine(")");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatMovePathText(
        string fallbackName,
        int version,
        int sourceByteCount,
        int decodedByteCount,
        CrossFireDatMovePath movePath)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"; decoded from {fallbackName}");
        builder.AppendLine($"; version {version}, path samples {movePath.Samples.Count}, bytes {sourceByteCount} -> {decodedByteCount}");
        builder.AppendLine();

        if (movePath.FirstFrame is { } firstFrame)
        {
            builder.AppendLine($"*SCENE_FIRSTFRAME {FormatNumber(firstFrame)}");
        }

        if (movePath.LastFrame is { } lastFrame)
        {
            builder.AppendLine($"*SCENE_LASTFRAME {FormatNumber(lastFrame)}");
        }

        if (movePath.FrameSpeed is { } frameSpeed)
        {
            builder.AppendLine($"*SCENE_FRAMESPEED {FormatNumber(frameSpeed)}");
        }

        builder.AppendLine("*MOVE_PATH");
        builder.AppendLine("{");
        if (movePath.AnimationFrameCount is { } animationFrameCount)
        {
            builder.AppendLine($"\t*ANIMATION_FRAME_COUNT {animationFrameCount.ToString(CultureInfo.InvariantCulture)}");
        }

        builder.AppendLine("\t*CONTROL_POS_SAMPLE");
        builder.AppendLine("\t{");
        foreach (CrossFireDatPathSample sample in movePath.Samples)
        {
            builder.AppendLine($"\t\t{FormatNumber(sample.Frame)} {FormatVector(sample.Position)}");
        }

        builder.AppendLine("\t}");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string FormatCameraText(
        string fallbackName,
        int version,
        int sourceByteCount,
        int decodedByteCount,
        CrossFireDatCamera camera)
    {
        int sampleCount = camera.Tracks.Sum(track => track.Samples.Count);
        var builder = new StringBuilder();
        builder.AppendLine($"; decoded from {fallbackName}");
        builder.AppendLine($"; version {version}, camera tracks {camera.Tracks.Count}, samples {sampleCount}, bytes {sourceByteCount} -> {decodedByteCount}");
        builder.AppendLine();

        if (camera.FirstFrame is { } firstFrame)
        {
            builder.AppendLine($"*SCENE_FIRSTFRAME {FormatNumber(firstFrame)}");
        }

        if (camera.LastFrame is { } lastFrame)
        {
            builder.AppendLine($"*SCENE_LASTFRAME {FormatNumber(lastFrame)}");
        }

        if (camera.FrameSpeed is { } frameSpeed)
        {
            builder.AppendLine($"*SCENE_FRAMESPEED {FormatNumber(frameSpeed)}");
        }

        if (camera.Tracks.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (CrossFireDatCameraTrack track in camera.Tracks)
        {
            builder.AppendLine($"*CAMERA_{track.Name}_ANIMATION");
            builder.AppendLine("{");
            if (track.LocalPosition is { } localPosition)
            {
                builder.AppendLine($"\t*LOCAL_POS {FormatVector(localPosition)}");
            }

            if (track.AnimationFrameCount is { } animationFrameCount)
            {
                builder.AppendLine($"\t*ANIMATION_FRAME_COUNT {animationFrameCount.ToString(CultureInfo.InvariantCulture)}");
            }

            if (track.Samples.Count > 0)
            {
                builder.AppendLine("\t*CONTROL_POS_SAMPLE");
                builder.AppendLine("\t{");
                foreach (CrossFireDatPathSample sample in track.Samples)
                {
                    builder.AppendLine($"\t\t{FormatNumber(sample.Frame)} {FormatVector(sample.Position)}");
                }

                builder.AppendLine("\t}");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatVector(CrossFireDatVector3 vector)
    {
        return $"{FormatNumber(vector.X)} {FormatNumber(vector.Y)} {FormatNumber(vector.Z)}";
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value % 1) < 0.000001
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string CleanLooseText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (ch >= ' ' && ch <= '~')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim();
    }
}
