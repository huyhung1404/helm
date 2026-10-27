// Port of sea-g2p src/lang/vi/resources.rs.
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// The static lookup tables of the Vietnamese normalizer: letter names, units, currencies, symbols, abbreviations and
/// the keyword sets that tell a date from a fraction. They are data rather than logic; adding an entry is the cheapest
/// way to fix a reading, and the entries here must stay exactly those of sea-g2p 0.10.0 for the phonemes to match.
/// </summary>
internal static class Resources
{
    /// <summary>Every Vietnamese letter that carries a diacritic, lowercase then uppercase.</summary>
    public const string ViLetters =
        "àáảãạăắằẳẵặâấầẩẫậèéẻẽẹêếềểễệìíỉĩịòóỏõọôốồổỗộơớờởỡợùúủũụưứừửữựỳýỷỹỵđÀÁẢÃẠĂẮẰẲẴẶÂẤẦẨẪẬÈÉẺẼẸÊẾỀỂỄỆÌÍỈĨỊÒÓỎÕỌÔỐỒỔỖỘƠỚỜỞỠỢÙÚỦŨỤƯỨỪỬỮỰỲÝỶỸỴĐ";

    private static Dictionary<string, string> Map(params string[] pairs)
    {
        var m = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2) m[pairs[i]] = pairs[i + 1];
        return m;
    }

    private static Dictionary<char, string> CharMap(params string[] pairs)
    {
        var m = new Dictionary<char, string>();
        for (var i = 0; i < pairs.Length; i += 2) m[pairs[i][0]] = pairs[i + 1];
        return m;
    }

    private static HashSet<string> Set(params string[] words) => new(words, StringComparer.Ordinal);

    public static readonly Dictionary<string, string> ViLetterNames = Map(
        "a", "a", "b", "bê", "c", "xê",
        "d", "đê", "đ", "đê", "e", "e",
        "ê", "ê", "f", "ép", "g", "gờ",
        "h", "hát", "i", "i", "j", "giây",
        "k", "ca", "l", "lờ", "m", "mờ",
        "n", "nờ", "o", "ô", "ô", "ô",
        "ơ", "ơ", "p", "phê", "q", "qui",
        "r", "rờ", "s", "ét", "t", "tê",
        "u", "u", "ư", "ư", "v", "vê",
        "w", "vê kép", "x", "ích", "y", "y",
        "z", "dét");

    public static readonly Dictionary<string, string> MeasurementKeyVi = Map(
        "km", "ki lô mét", "dm", "đê xi mét",
        "cm", "xen ti mét", "mm", "mi li mét",
        "nm", "na nô mét", "µm", "mic rô mét",
        "μm", "mic rô mét", "m", "mét",
        "kg", "ki lô gam", "g", "gam", "µg", "mic rô gam",
        "mg", "mi li gam", "km2", "ki lô mét vuông",
        "m2", "mét vuông", "cm2", "xen ti mét vuông",
        "mm2", "mi li mét vuông", "ha", "héc ta",
        "km3", "ki lô mét khối", "m3", "mét khối",
        "cm3", "xen ti mét khối", "mm3", "mi li mét khối",
        "l", "lít", "dl", "đê xi lít",
        "ml", "mi li lít", "hl", "héc tô lít",
        "kw", "ki lô oát", "mw", "mê ga oát",
        "gw", "gi ga oát", "kwh", "ki lô oát giờ", "kWh", "ki lô oát giờ",
        "mwh", "mê ga oát giờ", "wh", "oát giờ",
        "w", "oát",
        "hz", "héc", "khz", "ki lô héc",
        "mhz", "mê ga héc", "ghz", "gi ga héc",
        "pa", "pascal", "kpa", "kilopascal",
        "mpa", "megapascal", "bar", "bar",
        "mbar", "millibar", "atm", "atmosphere",
        "psi", "__start_en__p s i__end_en__", "j", "joule",
        "kj", "kilojoule", "cal", "calorie",
        "kcal", "kilocalorie", "h", "giờ",
        "p", "phút", "s", "giây", "sqm", "mét vuông",
        "cum", "mét khối", "gb", "gigabyte",
        "mb", "megabyte", "kb", "kilobyte",
        "tb", "terabyte", "db", "decibel",
        "oz", "ounce", "lb", "pound",
        "lbs", "pounds", "ft", "feet",
        "in", "inch", "dpi", "__start_en__d p i__end_en__",
        "ph", "phê hát", "gbps", "gigabits per second",
        "mbps", "megabits per second",
        "kbps", "kilobits per second",
        "gallon", "gallon", "mol", "mol",
        "mmol", "mi li mol",
        "ms", "mi li giây",
        "mah", "mi li am pe giờ", "ah", "am pe giờ",
        "mmhg", "mi li mét thuỷ ngân",
        "cmh2o", "xen ti mét nước",
        "µmol", "mic rô mol", "μmol", "mic rô mol",
        "meq", "__start_en__m e q__end_en__",
        "bpm", "__start_en__b p m__end_en__");

    /// <summary>Chemical element symbols, used to tell a formula ("NaCl") from a brand name ("MoMo").</summary>
    public static readonly HashSet<string> ChemicalElements = Set(
        "H", "He", "Li", "Be", "B", "C", "N", "O", "F", "Ne",
        "Na", "Mg", "Al", "Si", "P", "S", "Cl", "Ar", "K", "Ca",
        "Sc", "Ti", "V", "Cr", "Mn", "Fe", "Co", "Ni", "Cu", "Zn",
        "Ga", "Ge", "As", "Se", "Br", "Kr", "Rb", "Sr", "Y", "Zr",
        "Nb", "Mo", "Tc", "Ru", "Rh", "Pd", "Ag", "Cd", "In", "Sn",
        "Sb", "Te", "I", "Xe", "Cs", "Ba", "La", "Ce", "Pr", "Nd",
        "Pm", "Sm", "Eu", "Gd", "Tb", "Dy", "Ho", "Er", "Tm", "Yb",
        "Lu", "Hf", "Ta", "W", "Re", "Os", "Ir", "Pt", "Au", "Hg",
        "Tl", "Pb", "Bi", "Po", "At", "Rn", "Fr", "Ra", "Ac", "Th",
        "Pa", "U", "Np", "Pu", "Am", "Cm");

    /// <summary>
    /// Does the word parse as a chemical formula, every segment an element symbol with optional digits? A formula never
    /// repeats a two-letter symbol back to back, which is what frees "MoMo" and "BaBa" to be split as camelCase.
    /// </summary>
    public static bool IsChemicalFormula(string word)
    {
        if (word.Length == 0 || !RustText.IsAscii(word)) return false;
        var segs = new List<string>();
        var i = 0;
        while (i < word.Length)
        {
            var c = word[i];
            if (RustText.IsAsciiDigit(c)) { i++; continue; }
            if (!RustText.IsAsciiUpper(c)) return false;
            if (i + 1 < word.Length && RustText.IsAsciiLower(word[i + 1]))
            {
                var two = word.Substring(i, 2);
                if (!ChemicalElements.Contains(two)) return false;
                segs.Add(two);
                i += 2;
                continue;
            }
            var one = word.Substring(i, 1);
            if (!ChemicalElements.Contains(one)) return false;
            segs.Add(one);
            i++;
        }
        if (segs.Count < 2) return false;
        for (var k = 0; k + 1 < segs.Count; k++)
            if (segs[k].Length == 2 && segs[k] == segs[k + 1]) return false;
        return true;
    }

    public static readonly Dictionary<string, string> CurrencyKey = Map(
        "usd", "__start_en__u s d__end_en__", "vnd", "việt nam đồng",
        "vnđ", "việt nam đồng", "đ", "đồng",
        "v n d", "việt nam đồng", "v n đ", "việt nam đồng",
        "€", "euro", "euro", "euro",
        "eur", "euro", "¥", "yên",
        "yên", "yên", "jpy", "yên", "%", "phần trăm");

    public static readonly Dictionary<string, string> AcronymsExceptionsVi = Map(
        "CĐV", "cổ động viên", "HĐND", "hội đồng nhân dân",
        "HĐQT", "hội đồng quản trị", "TAND", "tòa án nhân dân",
        "BHXH", "bảo hiểm xã hội", "BHTN", "bảo hiểm thất nghiệp",
        "TP.HCM", "thành phố hồ chí minh", "VN", "việt nam",
        "UBND", "uỷ ban nhân dân", "TP", "thành phố",
        "HCM", "hồ chí minh", "HN", "hà nội",
        "BTC", "ban tổ chức", "CLB", "câu lạc bộ",
        "HTX", "hợp tác xã", "NXB", "nhà xuất bản",
        "TW", "trung ương", "CSGT", "cảnh sát giao thông",
        "LHQ", "liên hợp quốc", "THCS", "trung học cơ sở",
        "THPT", "trung học phổ thông", "ĐH", "đại học",
        "HLV", "huấn luyện viên", "GS", "giáo sư",
        "TS", "tiến sĩ", "TNHH", "trách nhiệm hữu hạn",
        "VĐV", "vận động viên", "TPHCM", "thành phố hồ chí minh",
        "PGS", "phó giáo sư", "SP500", "ét pê năm trăm",
        "PGS.TS", "phó giáo sư tiến sĩ", "GS.TS", "giáo sư tiến sĩ",
        "ThS", "thạc sĩ", "Th.S", "thạc sĩ", "BS", "bác sĩ",
        "KS", "kỹ sư",
        "UAE", "u a e", "CUDA", "cu đa",
        "QĐND", "quân đội nhân dân", "CAND", "công an nhân dân",
        "BCH", "ban chấp hành", "TBT", "tổng bí thư",
        "ĐHQG", "đại học quốc gia", "KCN", "khu công nghiệp",
        "GTVT", "giao thông vận tải", "TDTT", "thể dục thể thao",
        "BĐBP", "bộ đội biên phòng", "KHCN", "khoa học công nghệ",
        "BV", "bệnh viện", "BQP", "bộ quốc phòng",
        "GPLX", "giấy phép lái xe", "CMND", "chứng minh nhân dân",
        "CCCD", "căn cước công dân", "PCCC", "phòng cháy chữa cháy",
        "ATGT", "an toàn giao thông", "TNGT", "tai nạn giao thông",
        "BHYT", "bảo hiểm y tế", "ATTP", "an toàn thực phẩm",
        "ĐKKD", "đăng ký kinh doanh", "MST", "mã số thuế",
        "TN-MT", "tài nguyên môi trường", "TNMT", "tài nguyên môi trường",
        "GD-ĐT", "giáo dục đào tạo", "GDĐT", "giáo dục đào tạo",
        "KH-CN", "khoa học công nghệ",
        "LĐ-TB-XH", "lao động thương binh xã hội", "LĐTBXH", "lao động thương binh xã hội",
        "NN-PTNT", "nông nghiệp phát triển nông thôn", "NNPTNT", "nông nghiệp phát triển nông thôn",
        "VH-TT-DL", "văn hóa thể thao du lịch", "VHTTDL", "văn hóa thể thao du lịch",
        "TT-TT", "thông tin truyền thông", "TTTT", "thông tin truyền thông",
        "ĐBSCL", "đồng bằng sông cửu long", "MTTQ", "mặt trận tổ quốc",
        "ĐBQH", "đại biểu quốc hội", "VKS", "viện kiểm sát",
        "HS-SV", "học sinh sinh viên", "HSSV", "học sinh sinh viên",
        "SV", "sinh viên", "GV", "giáo viên",
        "CBCNV", "cán bộ công nhân viên",
        "CP", "cổ phần", "NĐ-CP", "nờ đê xê phê",
        "TT-BTC", "tê tê bê tê xê", "QĐ-TTg", "qui đê tê tê giê",
        "VKSND", "viện kiểm sát nhân dân", "GPMB", "giải phóng mặt bằng",
        "NSNN", "ngân sách nhà nước", "XKLĐ", "xuất khẩu lao động",
        "UBMTTQ", "uỷ ban mặt trận tổ quốc", "HĐLĐ", "hợp đồng lao động",
        "CNTT", "công nghệ thông tin", "TMĐT", "thương mại điện tử",
        "TNDN", "thu nhập doanh nghiệp", "TNCN", "thu nhập cá nhân",
        "GTGT", "giá trị gia tăng", "BCTC", "báo cáo tài chính",
        "ĐHĐCĐ", "đại hội đồng cổ đông", "TGĐ", "tổng giám đốc",
        "PTGĐ", "phó tổng giám đốc", "Cty", "công ty",
        "CTY", "công ty",
        "TNHH MTV", "trách nhiệm hữu hạn một thành viên",
        "GD&ĐT", "giáo dục đào tạo", "TN&MT", "tài nguyên môi trường",
        "KH&CN", "khoa học công nghệ", "LĐ-TB&XH", "lao động thương binh xã hội",
        "KH&ĐT", "kế hoạch đầu tư", "KHĐT", "kế hoạch đầu tư",
        "TT&TT", "thông tin truyền thông", "TTTT", "thông tin truyền thông",
        "VH-TT&DL", "văn hóa thể thao du lịch", "VHTTDL", "văn hóa thể thao du lịch",
        "CT&XH", "chính trị xã hội",
        "PT-TH", "phát thanh truyền hình",
        "PTTH", "phát thanh truyền hình",
        "TT-BYT", "tê tê bê y tê", "CT-TTg", "xê tê tê tê giê",
        "UBND-VP", "uỷ ban nhân dân vê phê",
        "QH11", "quốc hội mười một", "QH12", "quốc hội mười hai",
        "QH13", "quốc hội mười ba", "QH14", "quốc hội mười bốn",
        "QH15", "quốc hội mười lăm", "QH16", "quốc hội mười sáu");

    public static readonly Dictionary<string, string> TechnicalTerms = Map(
        "JSON", "__start_en__j__end_en__ son",
        "VRAM", "__start_en__v__end_en__ ram",
        "arXiv", "arxiv",
        "SaaS", "saas",
        "ASIAD", "a si át",
        "SEA Games", "sea games",
        "NVIDIA", "__start_en__n v d a__end_en__",
        "VN-Index", "__start_en__v n__end_en__ index",
        "MS DOS", "__start_en__m s__end_en__ dos",
        "MS-DOS", "__start_en__m s__end_en__ dos",
        "B2B", "__start_en__b__end_en__ two __start_en__b__end_en__",
        "MI5", "__start_en__m i__end_en__ five",
        "MI6", "__start_en__m i__end_en__ six",
        "2FA", "two __start_en__f a__end_en__",
        "TX-0", "__start_en__t x__end_en__ zero",
        "IPv6", "__start_en__i p v__end_en__ sáu",
        "IPv4", "__start_en__i p v__end_en__ bốn",
        "Washington D.C", "washington __start_en__d c__end_en__",
        "Washington DC", "washington __start_en__d c__end_en__",
        "KI", "ca i");

    /// <summary>Domain suffixes as Vietnamese speakers say them ("vê nờ", "i ô").</summary>
    public static readonly Dictionary<string, string> DomainSuffixMap = Map(
        "com", "com", "vn", "vê nờ",
        "net", "nét", "org", "o rờ gờ",
        "edu", "ê đu", "gov", "gờ o vê",
        "io", "i ô", "biz", "biz",
        "info", "info");

    public static readonly Dictionary<string, string> CurrencySymbolMap = Map(
        "$", "__start_en__u s d__end_en__",
        "€", "euro",
        "¥", "yên",
        "£", "pound",
        "₩", "won",
        "₫", "đồng");

    public static readonly Dictionary<char, int> RomanNumerals = new()
    {
        ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100, ['D'] = 500, ['M'] = 1000,
    };

    /// <summary>Abbreviations replaced as plain substrings, in no particular order (none overlaps another).</summary>
    public static readonly (string Key, string Value)[] Abbrs =
    [
        ("v.v", " vân vân"), ("v/v", " về việc"), ("đ/c", "địa chỉ"),
    ];

    public static readonly Dictionary<char, string> SymbolsMap = CharMap(
        "&", " và ", "+", " cộng ", "=", " bằng ",
        "#", " thăng ", ">", " lớn hơn ", "<", " nhỏ hơn ",
        "≥", " lớn hơn hoặc bằng ", "≤", " nhỏ hơn hoặc bằng ",
        "±", " cộng trừ ", "≈", " xấp xỉ ", "/", " trên ",
        "√", " căn bậc hai ", "∛", " căn bậc ba ", "∜", " căn bậc bốn ",
        "→", " đến ", "÷", " chia ", "*", " sao ",
        "×", " nhân ", "^", " mũ ", "~", " khoảng ",
        "%", " phần trăm ", "$", " đô la ", "€", " ê rô ",
        "£", " bảng ", "¥", " yên ", "₩", " won ",
        "₭", " kíp ", "₱", " bê xô ", "฿", " bạc ",
        "Ω", " ôm ", "@", " a còng ", "≠", " khác ",
        "∀", " với mọi ", "∏", " tích ", "∈", " thuộc ",
        "∃", " tồn tại ",
        "←", " ngược lại ", "↔", " tương đương ",
        "⇒", " suy ra ", "⇐", " ngược lại ", "⇔", " tương đương ",
        "∑", " tổng ", "∩", " giao ", "∪", " hội ",
        "¬", " phủ định ", "∞", " vô cùng ", "α", " an pha ",
        "β", " bê ta ", "γ", " ga ma ", "δ", " đen ta ",
        "ε", " ép si lon ", "ϵ", " thuộc ", "ζ", " de ta ",
        "η", " ê ta ", "θ", " thê ta ", "ι", " i ô ta ",
        "κ", " cáp ba ", "λ", " lam đa ", "ᴧ", " và ",
        "μ", " muy ", "Δ", " đen ta ", "ν", " nu ",
        "∆", " đen ta ",
        "ξ", " xi xi ", "ο", " o mi ron ", "π", " pi ",
        "ρ", " ro ", "σ", " xích ma ", "τ", " tao ",
        "υ", " úp si lon ", "φ", " phi ", "χ", " chi ",
        "ψ", " si ", "ω", " ô me ga ", "©", " bản quyền ",
        "∫", " tích phân ", "∮", " tích phân đường ",
        "∂", " đạo hàm riêng ", "∇", " nabla ",
        "∝", " tỉ lệ với ", "∠", " góc ",
        "⊥", " vuông góc với ", "∥", " song song với ",
        "⊂", " là tập con của ", "⊆", " là tập con của ",
        "⊃", " chứa ", "⊇", " chứa ",
        "∅", " tập rỗng ", "∉", " không thuộc ",
        "≡", " tương đương ", "≅", " đồng dạng ", "∼", " tương đương ",
        "∴", " suy ra ", "∵", " bởi vì ",
        "Σ", " xích ma ", "∑", " xích ma ",
        "⋅", " nhân ", "·", " nhân ", "∓", " trừ cộng ",
        "ℝ", " tập số thực ", "ℕ", " tập số tự nhiên ",
        "ℤ", " tập số nguyên ", "ℚ", " tập số hữu tỉ ",
        "ℂ", " tập số phức ",
        "½", " một phần hai ", "¼", " một phần tư ", "¾", " ba phần tư ",
        "⅓", " một phần ba ", "⅔", " hai phần ba ",
        "⅕", " một phần năm ", "⅖", " hai phần năm ", "⅗", " ba phần năm ", "⅘", " bốn phần năm ",
        "⅙", " một phần sáu ", "⅚", " năm phần sáu ",
        "⅛", " một phần tám ", "⅜", " ba phần tám ",
        "⅝", " năm phần tám ", "⅞", " bảy phần tám ");

    public static readonly Dictionary<char, string> SuperscriptsMap = CharMap(
        "⁰", " không ", "¹", " một ", "²", " bình phương ",
        "³", " lập phương ",
        "⁴", " mũ bốn ", "⁵", " mũ năm ",
        "⁶", " mũ sáu ", "⁷", " mũ bảy ", "⁸", " mũ tám ",
        "⁹", " mũ chín ",
        "ⁿ", " mũ n ", "ⁱ", " mũ i ",
        "⁻", " trừ ", "⁺", " cộng ",
        "ˣ", " mũ x ", "ʸ", " mũ y ", "ᵏ", " mũ k ",
        "ᵗ", " mũ t ");

    public static readonly Dictionary<char, string> SubscriptsMap = CharMap(
        "₀", " không ", "₁", " một ", "₂", " hai ",
        "₃", " ba ", "₄", " bốn ", "₅", " năm ",
        "₆", " sáu ", "₇", " bảy ", "₈", " tám ",
        "₉", " chín ",
        "ᵢ", " i ", "ⱼ", " j ", "ₐ", " a ", "ₑ", " e ",
        "ₒ", " o ", "ₓ", " x ", "ₕ", " h ", "ₖ", " k ",
        "ₗ", " l ", "ₘ", " m ", "ₙ", " n ", "ₚ", " p ",
        "ₛ", " s ", "ₜ", " t ", "ᵣ", " r ", "ᵤ", " u ",
        "ᵥ", " v ", "₊", " cộng ", "₋", " trừ ");

    public static readonly HashSet<string> WordLikeAcronyms = Set(
        "UNESCO", "NASA", "NATO", "ASEAN", "OPEC", "SARS", "FIFA", "UNIC", "RAM", "VRAM", "COVID", "IELTS", "STEM",
        "ROM",
        "ISO",
        "SEA",
        "UEFA", "EURO", "VAR", "ASIAD", "INTERPOL", "UNICEF",
        "TOEFL", "TOEIC", "PISA", "STEAM", "SAT", "GMAT",
        "EBITDA",
        "AIDS", "MERS", "ECMO", "LASIK",
        "FED", "NASDAQ", "UPCOM", "FOMO", "YOLO", "ASAP",
        "RADAR", "LASER", "LIDAR", "SONAR", "SCUBA", "GIF", "JPEG", "UNIX", "WIFI",
        "SIM", "LED", "VIP", "SPA", "GYM", "POS",
        "SWAT", "SEAL", "WASP", "COBOL", "BASIC", "OLED", "COVAX", "BRICS", "APEC", "VUCA", "PERMA", "DINK",
        "MENA", "EPIC", "OASIS", "BASE", "DART", "IDEA", "CHAOS", "SMART", "FANG", "BLEU", "REST", "ERROR",
        "SOTA", "BERT", "RAG", "ONNX", "ReLU", "ELO", "CAPTCHA",
        "ELISA", "SCADA", "MOSFET", "NEET", "REIT", "EBIT", "GINI", "NSAID", "PET",
        "SELECT", "FROM", "WHERE", "ORDER", "BY", "LIMIT", "OFFSET", "GROUP", "HAVING", "JOIN", "LEFT", "RIGHT",
        "INNER", "OUTER", "ON", "AS", "AND", "OR", "NOT", "IN", "BETWEEN", "LIKE", "IS", "NULL", "TRUE", "FALSE",
        "CASE", "WHEN", "THEN", "ELSE", "END", "UNION", "INTERSECT", "EXCEPT", "DESC");

    /// <summary>English acronyms joined by "&amp;" ("R&amp;D"), keyed by the uppercase run without the "&amp;".</summary>
    public static readonly HashSet<string> EnglishAmpersand = Set(
        "RD", "MA", "SP", "PG", "ATT", "HM", "FB", "QA", "RB", "TC",
        "JJ", "PL", "MM", "BW", "GT", "AE", "BQ", "DG", "SM", "BB", "PC");

    public static readonly Dictionary<string, string> CommonEmailDomains = Map(
        "gmail.com", "gmail chấm com",
        "yahoo.com", "yahoo chấm com",
        "yahoo.com.vn", "yahoo chấm com chấm vê nờ",
        "outlook.com", "outlook chấm com",
        "hotmail.com", "hotmail chấm com",
        "icloud.com", "icloud chấm com",
        "fpt.vn", "ép phê tê chấm vê nờ",
        "fpt.com.vn", "ép phê tê chấm com chấm vê nờ");

    /// <summary>Acronyms spelled with English letter names: the curated exception to the Vietnamese default.</summary>
    public static readonly string[] AcronymsSpellEn =
    [
        "IT", "US", "UK", "AI", "ID", "IP", "PC", "TV", "CD", "DVD",
        "CI",
        "USB", "GPS", "SUV", "CEO", "CFO", "CTO", "GDP", "FBI", "CIA", "NBA",
        "DJ", "IQ", "EQ", "MV", "EP", "SEO", "AM", "PM",
        "NY", "HP",
        "MIT", "UCLA", "NYU", "USC", "RMIT", "NUS", "NTU", "ANU", "LSE",
        "UN", "IMF", "WB", "ADB", "OECD", "UNDP", "UNHCR", "ILO", "IAEA",
        "IBM", "BMW", "LG", "MSI", "AMD", "TSMC", "VW", "GM",
        "BBC", "CNN", "HBO", "MTV", "ESPN", "NFL", "MLB", "UFC", "PSG",
        "HSBC", "ANZ", "UOB", "DBS", "KFC",
        "API", "SDK", "CPU", "GPU", "SSD", "HDD", "OS", "SSL", "HTTP", "HTML",
        "CSS", "SQL", "XML", "DNS", "LAN", "HDMI", "UI", "UX", "QR", "NFC",
        "VR", "AR", "GPT", "LLM", "OCR", "SMS", "MMS", "CMS", "CRM", "ERP",
        "GB", "TB", "MB", "KB",
        "KPI", "OKR", "IPO", "NDA", "MOU", "SME", "CV", "MBA", "ROE",
        "USD", "EUR", "GBP", "JPY", "CNY",
        "MC", "VJ", "BTS", "ASMR", "AFK", "GPA", "CBRE", "KOL", "PT",
        "ATM", "VPN", "CNC", "PCR", "LCD", "RGB", "IPS", "VGA", "FPS",
        "FTP", "MRI", "CT", "ECG", "EEG", "ICU", "BMI", "IVF", "HPV",
        "CPI", "FDI", "ODA", "FTA", "ETF", "ROA", "EPS",
        "ASR", "TTS", "AQI", "CTR", "ROAS", "ML", "DB", "RTX", "MRSA", "ASQ",
        "LSTM", "RNN", "NLP", "TPU", "SLM", "WER", "CER", "VAE", "MoE",
        "LGBT",
        "PE", "DOP", "SOP", "OEM",
        "ODM",
        "OT",
    ];

    /// <summary>Acronyms spelled with Vietnamese letter names where an earlier branch would otherwise claim them.</summary>
    public static readonly string[] AcronymsSpellVi = ["VTV", "VTC", "HTV", "EU", "QĐ", "VAT", "ROI"];

    /// <summary>The Vietnamese abbreviation table: Expand, then Fixed, then the mode-only groups; the first insert wins.</summary>
    public static readonly AbbrevTable ViAbbrev = BuildAbbrev();

    private static AbbrevTable BuildAbbrev()
    {
        var t = new AbbrevTable();
        foreach (var (k, v) in AcronymsExceptionsVi) t.Insert(k, new Reading(ReadingKind.Expand, v));
        foreach (var (k, v) in TechnicalTerms)
            if (!t.Contains(k)) t.Insert(k, new Reading(ReadingKind.Fixed, v));
        foreach (var k in WordLikeAcronyms)
            if (!t.Contains(k)) t.Insert(k, new Reading(ReadingKind.WordEn));
        foreach (var k in AcronymsSpellEn)
            if (!t.Contains(k)) t.Insert(k, new Reading(ReadingKind.LettersEn));
        foreach (var k in AcronymsSpellVi)
            if (!t.Contains(k)) t.Insert(k, new Reading(ReadingKind.LettersNative));
        return t;
    }

    /// <summary>Every Expand and Fixed key with its replacement text.</summary>
    public static readonly Dictionary<string, string> CombinedExceptions =
        ViAbbrev.ReplacementKeys().ToDictionary(k => k, k => ViAbbrev.Replacement(k)!, StringComparer.Ordinal);

    public static readonly HashSet<string> DateKeywords = Set(
        "vào", "ngày", "hôm", "hôm nay", "hôm qua", "hôm kia", "mai", "ngày mai", "ngày kia",
        "sinh", "sinh nhật", "kỷ niệm", "lễ", "tết", "diễn ra", "tổ chức", "thứ", "tuần", "tháng", "năm",
        "phiên", "mùng", "mồng");

    /// <summary>Date lead words, counted only immediately before a "d/m" cluster ("chiều 17/10").</summary>
    public static readonly HashSet<string> DateLeadWords = Set(
        "sáng", "trưa", "chiều", "tối", "đêm", "khuya", "rạng",
        "trước", "từ", "hết", "hạn", "đợt", "nghỉ", "lúc");

    /// <summary>Cue words that must sit right before a Roman numeral for it to be read as a number.</summary>
    public static readonly HashSet<string> RomanKeywords = Set(
        "kỷ", "kỉ", "chương", "phần", "hồi", "quyển", "tập", "kỳ", "kì",
        "khoản", "điều", "mục", "đời", "vua", "chiến", "hội", "khóa", "khoá",
        "đệ", "triều", "quý", "lần", "vòng", "thứ",
        "louis", "napoléon", "napoleon", "henry", "george", "charles",
        "elizabeth", "edward", "william", "james", "richard", "john",
        "philip", "philippe", "frederick", "ferdinand", "peter", "pierre",
        "catherine", "pius", "benedict", "leo", "gregory", "otto");

    public static readonly HashSet<string> MathKeywords = Set(
        "cộng", "trừ", "nhân", "chia", "bằng", "sin", "cos", "tan", "log", "sqrt", "xác suất", "tỷ lệ", "tỉ lệ");
}
