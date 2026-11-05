using CFRezManager;
using System.IO;

static partial class Program
{
    static void RunVariantValidationChecks(string run)
    {
        string source = Path.Combine(run, "variant-validation-source"), texture = "ModelTextures/VariantValidation/Declared.dtx";
        string baseModel = "Models/PLAYERVIEW/PV-VariantValidation", targetModel = baseModel + "_GR";
        string physicalModel = targetModel + ".ltb";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, texture))!); File.WriteAllBytes(Path.Combine(source, texture), SocketDtx(73));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, physicalModel))!); File.WriteAllBytes(Path.Combine(source, physicalModel), NanoModel([0]));
        var root = (ExplorerItem)Call("LithTechObjExportCommand", "BuildDirectoryTree", source)!;
        var files = (IEnumerable<ExplorerItem>)Call("LithTechObjExportCommand", "EnumerateFiles", root)!;
        ExplorerItem textureItem = files.Single(f => Key(f.OutputRelativePath) == Key(texture)), modelItem = files.Single(f => Key(f.OutputRelativePath) == Key(physicalModel));
        var mesh = new LithTechMesh("part-0", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2], [new(0, 0), new(1, 0), new(0, 1)], TextureSlotIndices: [0], RenderStyleIndex: 0, HasAuthoritativeTextureBindings: true);
        var doc = new LithTechModelDocument("Source", [mesh], "fixture", 0, 0);
        var typed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, ExplorerItem[]>(StringComparer.OrdinalIgnoreCase);
        var decoded = new Dictionary<string, LithTechModelDocument?>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, ExplorerItem[]>(StringComparer.OrdinalIgnoreCase) { [texture] = [textureItem] };
        var verified = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var direct = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var contexts = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var inferred = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var inferredContexts = new Dictionary<string, Dictionary<int, string?>>(StringComparer.OrdinalIgnoreCase);
        var specific = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Bind() => Call("LithTechModelTextureConfigIndex", "AddLtcPreviewVariantTextureBindings", baseModel, "PViewModelFileName", texture, 0, new[] { 0 }, new[] { doc }, Array.Empty<int>(), new[] { "variant:check" }, false, typed, models, decoded, textures, verified, direct, contexts, inferred, inferredContexts, specific);
        models[targetModel] = [modelItem]; Bind(); Check("untyped variants do not decode texture copies", verified.Count == 0 && inferred.Count == 0);
        typed.Add(targetModel); models.Clear(); Bind(); Check("absent variant models do not decode texture copies", verified.Count == 0 && inferred.Count == 0);
        models[targetModel] = [modelItem]; direct[targetModel] = new() { [0] = "ModelTextures/VariantValidation/Direct.dtx" }; Bind();
        Check("directly bound variants do not decode unused inferred textures", verified.Count == 0 && inferred.Count == 0);
        direct.Clear(); Bind();
        Check("eligible unbound variants still validate every texture copy", verified.Count == 1 && verified.Values.Single());
        Check("eligible variant keeps the verified texture binding", inferred.TryGetValue(targetModel, out var slots) && Key(slots[0]!) == Key(texture));
        File.WriteAllBytes(Path.Combine(source, texture), [1, 2, 3]); verified.Clear(); inferred.Clear(); inferredContexts.Clear(); Bind();
        Check("invalid variant texture prevents inferred bindings", verified.Count == 1 && !verified.Values.Single() && inferred.Count == 0 && inferredContexts.Count == 0);
    }
}
