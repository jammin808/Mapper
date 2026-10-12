using System.Collections.Immutable;
using PlayMap.Core.Vision;

namespace PlayMap.Vision.Models;

/// <summary>The models a node knows how to fetch and run. Built-in entries name the publisher's licence and the
/// files; an entry whose checksums are not filled in (<see cref="VisionModelInfo.IsVerified"/> false) is listed
/// so the operator can see it, but the store refuses to install it. An operator can add an entry (a local mirror,
/// a newer export) with its own checksums. Nothing here downloads; the store does, when the engine asks.</summary>
public sealed class ModelCatalog
{
    private readonly ImmutableArray<VisionModelInfo> _models;

    public ModelCatalog(IEnumerable<VisionModelInfo> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var list = models.ToImmutableArray();
        foreach (var m in list)
            ModelStore.ValidateEntry(m);
        if (list.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != list.Length)
            throw new ArgumentException("Model ids are unique.", nameof(models));
        _models = list;
    }

    public ImmutableArray<VisionModelInfo> Models => _models;

    public VisionModelInfo? Find(string id) => _models.FirstOrDefault(m => m.Id == id);

    /// <summary>This catalogue with an entry added or replaced by id.</summary>
    public ModelCatalog With(VisionModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ModelCatalog(_models.Where(m => m.Id != model.Id).Append(model));
    }

    /// <summary>The model to offer first for a task: never a non-commercial licence and never one gated behind
    /// the publisher's own agreement — the operator chooses those explicitly.</summary>
    public VisionModelInfo? DefaultFor(VisionTask task) =>
        _models.FirstOrDefault(m => m.Task == task && m.Licence.AllowsCommercialUse && !m.RequiresSeparateAgreement);

    public static readonly VisionLicence Apache2 = new(
        "Apache-2.0", "Apache License 2.0", "https://www.apache.org/licenses/LICENSE-2.0", LicenceKind.Permissive,
        "Use, modify and redistribute, commercially too; keep the notice and the licence text.");

    public static readonly VisionLicence CcByNc4 = new(
        "CC-BY-NC-4.0", "Creative Commons Attribution-NonCommercial 4.0", "https://creativecommons.org/licenses/by-nc/4.0/",
        LicenceKind.NonCommercial, "No commercial use: not for paid shows or client work.");

    public static readonly VisionLicence SamLicence = new(
        "SAM-License", "SAM License (Meta)", "https://github.com/facebookresearch/sam3/blob/main/LICENSE", LicenceKind.Custom,
        "The publisher's own terms; read them in full before accepting.");

    /// <summary>The built-in catalogue (declared after the licences: static initialisers run in text order).</summary>
    public static ModelCatalog Default { get; } = new(BuiltIn());

    // Verify before release: none of the URLs, sizes or checksums below have been checked against the publisher
    // from this tree (no network to the hosts at authoring). Checksums are left null so the store refuses these
    // entries until someone fills in values they have verified; sizes are approximate and informational only.
    private static IEnumerable<VisionModelInfo> BuiltIn() =>
    [
        new VisionModelInfo(
            "sam2.1-tiny", "SAM 2.1 Hiera Tiny (ONNX)", VisionTask.PromptSegmentation, Apache2,
            [
                new VisionModelFile("encoder", "sam2.1_hiera_tiny.encoder.onnx",
                    "https://huggingface.co/vietanhdev/segment-anything-2.1-onnx-models/resolve/main/sam2.1_hiera_tiny.encoder.onnx", null, 134_000_000),
                new VisionModelFile("decoder", "sam2.1_hiera_tiny.decoder.onnx",
                    "https://huggingface.co/vietanhdev/segment-anything-2.1-onnx-models/resolve/main/sam2.1_hiera_tiny.decoder.onnx", null, 21_000_000),
            ],
            1024, 1024, false,
            "Clicks and boxes to masks; the decoder must keep the previous-mask input for click refinement. Verify before release: URL paths and checksums."),
        new VisionModelInfo(
            "sam2.1-small", "SAM 2.1 Hiera Small (ONNX)", VisionTask.PromptSegmentation, Apache2,
            [
                new VisionModelFile("encoder", "sam2.1_hiera_small.encoder.onnx",
                    "https://huggingface.co/vietanhdev/segment-anything-2.1-onnx-models/resolve/main/sam2.1_hiera_small.encoder.onnx", null, 163_000_000),
                new VisionModelFile("decoder", "sam2.1_hiera_small.decoder.onnx",
                    "https://huggingface.co/vietanhdev/segment-anything-2.1-onnx-models/resolve/main/sam2.1_hiera_small.decoder.onnx", null, 21_000_000),
            ],
            1024, 1024, false,
            "Clicks and boxes to masks, a little sharper than tiny. Verify before release: URL paths and checksums."),
        new VisionModelInfo(
            "sam3", "SAM 3 (text prompts)", VisionTask.TextSegmentation, SamLicence,
            [
                new VisionModelFile("model", "sam3.onnx", "https://huggingface.co/facebook/sam3", null, 0),
            ],
            1008, 1008, true,
            "A phrase to every matching instance (\"windows\", \"columns\"). Gated by the publisher: accept their agreement on their site first. No verified ONNX export is known to this catalogue; the URL is a placeholder."),
        new VisionModelInfo(
            "depth-anything-3-small", "Depth Anything 3 Small (ONNX)", VisionTask.Depth, Apache2,
            [
                new VisionModelFile("model", "da3-small.onnx", "https://huggingface.co/depth-anything/DA3-SMALL", null, 100_000_000),
            ],
            518, 518, false,
            "Rough depth from one photo for previs. Verify before release: an ONNX export, its URL, input size and checksum."),
        new VisionModelInfo(
            "depth-anything-3-base", "Depth Anything 3 Base (ONNX)", VisionTask.Depth, Apache2,
            [
                new VisionModelFile("model", "da3-base.onnx", "https://huggingface.co/depth-anything/DA3-BASE", null, 400_000_000),
            ],
            518, 518, false,
            "Rough depth from one photo for previs, finer than small. Verify before release: an ONNX export, its URL, input size and checksum."),
        new VisionModelInfo(
            "depth-anything-3-large", "Depth Anything 3 Large (ONNX, non-commercial)", VisionTask.Depth, CcByNc4,
            [
                new VisionModelFile("model", "da3-large.onnx", "https://huggingface.co/depth-anything/DA3-LARGE", null, 1_300_000_000),
            ],
            518, 518, false,
            "Non-commercial licence: never offered by default, installed only with an explicit non-commercial acknowledgement. Verify before release."),
    ];
}
