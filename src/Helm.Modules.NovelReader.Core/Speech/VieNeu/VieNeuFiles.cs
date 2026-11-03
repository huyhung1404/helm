using Helm.Core.Downloads;

namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>
/// Where the VieNeu voice lives on this device and what it is made of: VieNeu-TTS v3 Turbo's int8 ONNX export, the MOSS
/// audio codec's decoder and sea-g2p's dictionary, each pinned to the revision Helm was tested with (all Apache-2.0).
/// About 270 MB, downloaded once when the voice is first wanted. A file counts only after its hash was checked
/// (<see cref="PinnedFiles.IsInstalled"/> looks for the mark left then), so a cut-off download is never loaded.
/// </summary>
public sealed class VieNeuFiles(string folder, IReadOnlyList<PinnedFile>? files = null) : PinnedFiles(folder, files ?? All)
{
    private const string Model = "https://huggingface.co/pnnbao-ump/VieNeu-TTS-v3-Turbo/resolve/61b85e3d937fbbacb387714180e8182823512523/onnx_int8/";
    private const string Codec = "https://huggingface.co/OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX/resolve/ceff0d0749bfb3fa2d61149794ec6feef0d1e1ae/";
    private const string Dictionary = "https://raw.githubusercontent.com/pnnbao97/sea-g2p/e825173f235d08ea19315b2b279fb11153b44cea/python/sea_g2p/";

    public static IReadOnlyList<PinnedFile> All { get; } =
    [
        new("v3/config.json", Model + "config.json", 2152, "a9f8d9c4b4736448ab355d1a98cfe48f5e39aecf2916c37b0806c228612e9a2d"),
        new("v3/tokenizer.json", Model + "tokenizer.json", 22320, "6cc6bcbe380b8c37bd9f2514e37c5dfa3e00e122c6e3125dae5c4afe48e39158"),
        new("v3/vieneu_prefill.onnx", Model + "vieneu_prefill.onnx", 1090823, "c6a80dabf67c820de798f8deb7d4e0f37d81b5d76e33fbe20ab5a67f2d371f4e"),
        new("v3/vieneu_decode_step.onnx", Model + "vieneu_decode_step.onnx", 1062040, "2c5b30bd8ccb751c58d651f44c074df10c4113efd08719adaa8e3dec6a6ce2ca"),
        new("v3/vieneu_acoustic_cached.onnx", Model + "vieneu_acoustic_cached.onnx", 7207223, "f631e3387c788c3d8b9a5ac5df94952af5bc4c4d1049ff8a751e76a246fff2d4"),
        new("v3/vieneu_backbone_shared.data", Model + "vieneu_backbone_shared.data", 103891968, "bb683925f7c8d826fadca4f8a0252ae4d5fc5b7837c14f6857e18f4c6666588d"),
        new("v3/vieneu_v3_heads.npz", Model + "vieneu_v3_heads.npz", 52219622, "fb22484baa424bbb775133a6e5f0d00d6299b2b256fbe3312a864b85b9aed01e"),
        new("codec/moss_audio_tokenizer_decode_full.onnx", Codec + "moss_audio_tokenizer_decode_full.onnx", 681902,
            "0fbbafe3fd4afa2a019af5c5ced204af6e2d1db044fa40f021525d2aee95b4ac"),
        new("codec/moss_audio_tokenizer_decode_shared.data", Codec + "moss_audio_tokenizer_decode_shared.data", 44198912,
            "e69d52e0f4e84ca27850557ee54face46632d3a5a16c89bd246c7c408466dcad"),
        new("sea_g2p.bin", Dictionary + "sea_g2p.bin", 62829820, "4346e690d0711ebc5231e7a42c5c88aaf6e40377e894b4617c018fd81c6f4096"),
    ];

    public string ModelFolder => System.IO.Path.Combine(Folder, "v3");

    public string CodecFolder => System.IO.Path.Combine(Folder, "codec");

    public string DictionaryFile => System.IO.Path.Combine(Folder, "sea_g2p.bin");
}
