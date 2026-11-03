# Notes: search by meaning

"Ghi chú về lỗi server hôm trước" finds the note about Nginx answering 502, though it has none of those words. It runs
in Helm, offline, with no paid API.

## The model

- **paraphrase-multilingual-MiniLM-L12-v2** (sentence-transformers, Apache-2.0; 50+ languages, Vietnamese and English
  among them), in Xenova's int8 ONNX export `onnx/model_quantized.onnx` with its `tokenizer.json`, pinned to revision
  `2c4055b12046f11709e9df2c122e59ffbdc2f900` of `huggingface.co/Xenova/paraphrase-multilingual-MiniLM-L12-v2`, each
  file checked by SHA-256 (`src/Helm.Embeddings/SentenceModelFiles.cs`). 130 MB in all.
- Chosen over multilingual-e5-small (MIT, same size): on Helm's sample notes it put the right note first as often, and
  its scores spread wider (unrelated notes 0–0.3, related 0.35–0.7 against e5's 0.75–0.9 for everything), so a note can
  be found by meaning alone with a fixed threshold (`NoteRecall.MinMeaning`). Every multilingual model this size carries
  XLM-R's 250,000-piece vocabulary, so nothing good for Vietnamese is under ~100 MB.
- Downloaded only when the user presses Download in Notes' settings ("Search by meaning"), into
  `<data folder>/models/paraphrase-multilingual-minilm`, with progress, Stop, going on where it stopped, and Remove
  (`Helm.Core.Downloads.PinnedDownload`, the VieNeu voice's download made generic). Never in the installer or the APK.
- The tokenizer (`UnigramTokenizer`, `PrecompiledCharsmap`) is a C# port of what Hugging Face tokenizers does with this
  tokenizer.json; tests compare its ids with the reference (`tests/Helm.Tests/Fixtures/sentence-model`, made by
  `reference.py` there) and the vectors with onnxruntime in Python.
- On a PC: about 0.5 s to load (the first search after five idle minutes), 5–10 ms a short note, 2–5 ms a search.
  One text per run (the int8 model scales a run's numbers together, so batching with padding changed vectors).

## The index

- `notes.vectors` (synced, end-to-end encrypted, in the Notes sync group): one record per note id with the model id, a
  hash of the note's title and text, and a vector per part of the text (whole words, at most 126 tokens a part, at most
  32 parts), each 384 numbers as signed bytes in base64 (512 characters).
- `NoteIndexer` runs on any device that has the model, 15 s after notes stop changing: it embeds the notes whose hash
  or model differs, one at a time, and drops vectors of notes deleted for good. On the phone it waits while Helm is in
  the background.

## The search

- `NoteMeaning` embeds the search on this device and gives each note the cosine of its closest part. `NoteRecall.Rank`
  blends meaning + 0.6 × words + 0.1 × recency; a note without the words is found from 0.4, or from 0.3 when it is
  within 0.05 of the closest note (a search padded with "ghi chú về… hôm trước" is less close to everything).
- `notes_search` (agents, Ask Helm) uses the query, else its `any` words together. The Notes search box lists the notes
  with the words as before, then, a moment after typing stops, the notes about the same thing.
- Without the model on this device (or before any vectors exist), both search by words exactly as before.

## The phone

The phone downloads the model too, when the user asks (choice (a)): a search needs the search's own vector, made where
the search is. The other way, asking a PC for the vector through the agent job channel, would need a PC online and a
sync round trip for every keystroke-pause, for a file the phone can hold (VieNeu is twice its size). What the phone
gets from the PC are the notes' vectors, so with the model it only reads notes no other device has read. A phone
without the model searches by words.
