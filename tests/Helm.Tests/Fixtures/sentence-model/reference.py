# Reference token ids and vectors for Helm's tests, from Hugging Face tokenizers and onnxruntime.
# usage: python -I reference.py <pylib> <model dir> <out dir>
import sys, json, gzip, unicodedata
sys.path.insert(0, sys.argv[1])
import numpy as np, onnxruntime as ort
from tokenizers import Tokenizer
model, out = sys.argv[2], sys.argv[3]
tok = Tokenizer.from_file(model + "/tokenizer.json")
samples = [
    "Ghi chú về lỗi server hôm trước",
    "VPS sập lúc 3 giờ sáng\nNginx trả 502, pm2 restart thì hết.",
    "Công thức phở bò: xương bò hầm 6 tiếng, quế hồi gừng nướng.",
    unicodedata.normalize("NFD", "Tiếng Việt viết kiểu tổ hợp: Điện thoại của tôi"),
    "Meeting notes with the design team about the new onboarding flow",
    "Server down again: the API returned 500 errors after the deploy, rolled back",
    "  hai   khoảng trắng\tvà tab  ",
    "ＦＵＬＬＷＩＤＴＨ ｶﾀｶﾅ ①②③ ﬁ ½",
    "Emoji 😀👍🏽 and 中文 汉字, 日本語のテキスト",
    "unknown \u0f3a\u0f3b and \U0001d11e music \u200b zero width",
    "email hung@example.com, URL https://huyhung1404.com/a?b=c#d",
    "C# code: var x = list.Where(n => n > 0).ToList();",
    "Đ đ Ơ ơ Ư ư Ă ă Â â Ê ê Ô ô — “ngoặc kép” ‘đơn’ … ellipsis",
    "a▁b ▁lead",
]
ref = []
for s in samples:
    e = tok.encode(s)
    ref.append({"text": s, "ids": e.ids})
# A cut-down tokenizer.json with every piece that could match inside the samples (the rest as empty stand-ins, so the
# ids stay), the lowest score kept, and the full character map.
d = json.load(open(model + "/tokenizer.json", encoding="utf8"))
norm = tok.normalizer
words = set()
for s in samples:
    for w, _ in tok.pre_tokenizer.pre_tokenize_str(norm.normalize_str(s)):
        words.add(w)
vocab = d["model"]["vocab"]
low = min(range(len(vocab)), key=lambda i: vocab[i][1])
keep = {0, 1, 2, 3, len(vocab) - 1, low}
for i, (p, _) in enumerate(vocab):
    if p and any(p in w for w in words):
        keep.add(i)
d["model"]["vocab"] = [v if i in keep else ["", 0.0] for i, v in enumerate(vocab)]
with gzip.open(out + "/tokenizer-cut.json.gz", "wt", encoding="utf8") as f:
    json.dump(d, f, ensure_ascii=False)
print("kept", len(keep), "pieces")
# Vectors from the int8 model, for the real-model test.
s = ort.InferenceSession(model + "/model_quantized.onnx", providers=["CPUExecutionProvider"])
texts = samples[:6]
vecs = []
for t in texts:
    e = tok.encode(t)
    ids = np.array([e.ids], np.int64); m = np.ones_like(ids)
    h = s.run(None, {"input_ids": ids, "attention_mask": m, "token_type_ids": np.zeros_like(ids)})[0]
    v = h[0].mean(0); v = v / np.linalg.norm(v)
    vecs.append([round(float(x), 6) for x in v])
NL = chr(10)
lines = ["{", ' "tokens": [']
lines.append(("," + NL).join("  " + json.dumps(r, ensure_ascii=False) for r in ref))
lines += [" ],", ' "vectors": [']
lines.append(("," + NL).join("  " + json.dumps({"text": t, "vector": v}, ensure_ascii=False) for t, v in zip(texts, vecs)))
lines += [" ]", "}"]
with open(out + "/reference.json", "w", encoding="utf8", newline=NL) as f:
    f.write(NL.join(lines) + NL)
