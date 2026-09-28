# Hướng dẫn dùng Vault

Vault giữ mật khẩu, ghi chú bí mật, thẻ ngân hàng, giấy tờ tùy thân và tài liệu trong Helm, trên PC và Android.
Mọi thứ được mã hóa ngay trên thiết bị trước khi đồng bộ hay sao lưu: máy chủ sync, thư mục backup và bất kỳ ai
sao chép chúng chỉ thấy dữ liệu đã mã hóa. Cách hoạt động bên trong nằm ở [vault-design.md](vault-design.md).

Tên nút và cài đặt được giữ nguyên tiếng Anh, in đậm, đúng như trên màn hình.

- [Ba thứ bí mật](#ba-thứ-bí-mật)
- [Cài đặt lần đầu](#cài-đặt-lần-đầu)
- [Dùng hằng ngày](#dùng-hằng-ngày)
- [Khóa và mở khóa nhanh](#khóa-và-mở-khóa-nhanh)
- [Sao lưu](#sao-lưu)
- [Khi có sự cố](#khi-có-sự-cố)
- [Cài đặt](#cài-đặt)
- [Danh sách kiểm tra](#danh-sách-kiểm-tra)

## Ba thứ bí mật

Vault dùng ba thứ bí mật khác nhau. Đừng nhầm lẫn chúng:

| Bí mật | Dùng để mở | Nếu mất |
|---|---|---|
| **Mật khẩu vault** (ít nhất 14 ký tự) | Vault, trên mọi thiết bị | Mở bằng recovery key, rồi đặt mật khẩu mới |
| **Recovery key** `HELMV-…`, in trên Emergency Kit | Vault, khi quên mật khẩu | Tạo key mới trong phần cài đặt, khi vẫn còn nhớ mật khẩu |
| **Sync passphrase** (Helm Sync, trong General) | Đồng bộ giữa các thiết bị, không mở được vault | Xem phần Helm Sync; vault vẫn khóa bằng mật khẩu riêng của nó |

> **Mất cả mật khẩu vault lẫn Emergency Kit là mất vault vĩnh viễn.** Không ai đặt lại được: không phải Helm,
> không phải máy chủ, không phải quản trị viên. Đó là cái giá để không ai khác đọc được vault.

Hãy chọn mật khẩu dễ nhớ với chính mình nhưng người khác không đoán được: vài từ không liên quan ghép lại là cách
tốt. Vault từ chối mật khẩu yếu, kể cả từ thông dụng, tên người, năm sinh, phím liền nhau và kiểu
"Password@2026!!".

## Cài đặt lần đầu

### Trên thiết bị đầu tiên

1. Mở **Vault** từ menu hoặc từ **Quick access** ở Home. Nếu thấy báo Vault đang tắt, mở **Settings** và bật
   **Enable Vault**.
2. Ở **Create your vault**, gõ mật khẩu vault hai lần rồi chọn **Create vault**.
3. **Lưu Emergency Kit.** Kit chứa recovery key và **chỉ hiện một lần**.
   - PC: **Print the Emergency Kit…** (hoặc in ra PDF và cất file ở chỗ không nối mạng, ví dụ USB).
   - Android: **Save the Emergency Kit…** rồi cất file ở chỗ không nối mạng.
   Sau đó gõ lại recovery key vào ô xác nhận và chọn **I saved it**.
4. Mở **Settings** → **Backup** → **Backup folder** và chọn một thư mục, ví dụ thư mục Google Drive for desktop
   hoặc OneDrive trên PC, thư mục Google Drive trên Android. Xem [Sao lưu](#sao-lưu).
5. Tùy chọn: bật **Unlock with Windows Hello** (PC) hoặc **Unlock with fingerprint or face** (Android).

Cất Emergency Kit tách khỏi các thiết bị mà nó bảo vệ: bản giấy trong ngăn kéo, hoặc file PDF trên USB. Đừng để nó
chung thư mục đám mây với các bản backup.

### Trên thiết bị khác

1. Cài **Helm Sync** trong General với cùng tài khoản và sync passphrase như ở thiết bị đầu tiên.
2. Mở **Vault**. Vault tìm thấy kho mà sync mang về và hiện **Vault is locked**.
3. Mở khóa bằng **đúng mật khẩu vault đó**. Không tạo vault thứ hai.
4. Có thể chọn thư mục backup trên thiết bị này để có thêm một bản sao (không bắt buộc; một thiết bị là đủ).

Vault vẫn dùng được trên một thiết bị mà không cần sync. Khi đó tài liệu nằm trên thiết bị này cho đến khi cài sync.

## Dùng hằng ngày

### Thêm mục

Chọn **+** rồi chọn loại:

| Loại | Dùng cho | Có sẵn các trường |
|---|---|---|
| **Login** | Website và ứng dụng | Username, Password, Website |
| **Secure note** | Mật khẩu Wi-Fi, câu trả lời bảo mật, mọi thứ riêng tư | Notes |
| **Card** | Thẻ ngân hàng | Cardholder, Number, Expiry, CVV, PIN |
| **Identity** | CCCD, hộ chiếu, bằng lái | Full name, ID number, Date of birth, Phone, Email, Address |
| **Document** | Bản scan và file (PDF, ảnh, bất kỳ loại nào) | Attach a document… |

Mỗi mục có thể thêm trường (**Add field**: Text, Username, Password, Email, Phone, Website, Hidden cho mã PIN hay
mã số, Several lines), ghi chú, tag, tài liệu đính kèm và đánh dấu **Favorite**. Xong thì chọn **Save**.

- **Tạo mật khẩu mạnh**: nút đũa phép cạnh ô mật khẩu điền sẵn một mật khẩu ngẫu nhiên.
- **Icon**: **Choose icon…** gán ảnh riêng cho mục (PNG, JPEG hoặc WebP, tối đa 256 KB), được mã hóa cùng mục.
  Không có ảnh thì mục hiện chữ cái đầu của tên trên một màu lấy theo tên.

### Hai tab: Logins & tokens và Other

Danh sách chia làm hai tab ngay dưới Search:

- **Logins & tokens**: các mục dùng để đăng nhập (**Login**) và các **Token** (API key, access token), mỗi mục một
  dòng: icon, tên, username (token thì hiện chữ "Token") và mật khẩu hoặc token đang ẩn. Trên mỗi dòng:
  - **con mắt** hiện hoặc ẩn mật khẩu / token;
  - **Copy** sao chép mật khẩu / token và đổi thành **✓ Copied** trong 2 giây.

  Login chưa có mật khẩu thì dòng đó không hiện ô mật khẩu.
- **Other**: mọi thứ còn lại (ghi chú, thẻ, giấy tờ, tài liệu, ảnh), mỗi dòng có ảnh của mục, hoặc icon của loại mục
  nếu chưa gán ảnh.

Chọn một dòng để mở mục đó. Ô **Show** dưới hai tab lọc trong tab đang mở: **All**, **Logins**, **Tokens**,
**Favorites** và **Trash** ở tab đầu; **All**, **Notes**, **Cards**, **Identities**, **Documents**, **Favorites** và
**Trash** ở tab Other. Mục mới tạo tự mở đúng tab của nó. **Search** tìm trong tab đang mở, theo tên, tag, tên tài
liệu, username, email và website, không bao giờ tìm trong mật khẩu, token hay các giá trị ẩn khác.

### Token (API key)

Chọn **+** → **Token (API key)**: mục chỉ có một trường ẩn tên **Token**, dán token vào rồi **Save**. Cần thêm thông tin
(website, ngày hết hạn…) thì dùng **Add field**.

Token đã lỡ lưu dạng Login: mở mục, chọn **Edit**, đổi **Type** thành **Token** rồi **Save**. Các trường giữ nguyên
và bản trước vẫn nằm trong lịch sử. **Type** đổi được cho mọi loại mục, nó chỉ quyết định mục nằm ở tab nào.

> Máy nào còn chạy Helm bản cũ hơn sẽ chưa đọc được Token: bản cũ báo mục đó là "cannot be opened" nhưng vẫn giữ nguyên,
> không xoá. Cập nhật Helm trên máy đó là thấy lại.

### Sao chép

Mật khẩu đã copy bị xóa khỏi clipboard sau 30 giây (nếu clipboard vẫn còn giữ nó). Trên Windows, mật khẩu không bao
giờ vào lịch sử clipboard (Win+V) hay clipboard đám mây. Trên Android 13 trở lên, nó được đánh dấu nhạy cảm nên bàn
phím không hiện ra.

### Tài liệu

**Attach a document…** thêm file vào một mục. File được mã hóa trên thiết bị rồi mới tải lên; giới hạn dung lượng lấy
theo tài khoản Helm Sync và được báo khi file quá lớn.

- **Open** mở một bản đã giải mã bằng ứng dụng tương ứng. Bản này nằm trong thư mục tạm riêng của Helm và bị xóa khi
  vault khóa và khi Helm khởi động. Ứng dụng kia có thể giữ bộ nhớ đệm riêng, nên chỉ dùng **Save a copy…** khi thật
  sự cần file ở ngoài Vault.
- **Save a copy…** ghi một bản đã giải mã ra chỗ tự chọn. Bản đó do người dùng tự bảo quản.
- **Remove** gỡ tài liệu khỏi mục. Các phiên bản cũ trong lịch sử vẫn còn giữ nó.

### Lịch sử và thùng rác

- Mỗi lần lưu, phiên bản trước được giữ lại: mở **History** ở cuối mục và chọn **Restore** ở phiên bản cần lấy lại.
  Vault giữ 10 phiên bản gần nhất.
- **Move to trash** là cách duy nhất để xóa. Mục nằm trong **Trash** 30 ngày, ở đó có thể **Restore** hoặc
  **Delete for good**.

## Khóa và mở khóa nhanh

Vault tự khóa:

- sau 5 phút không dùng (**Lock after inactivity**);
- trên PC, khi Windows khóa, đăng xuất hoặc máy ngủ (**Lock when Windows locks**);
- trên Android, 30 giây sau khi rời Helm hoặc tắt màn hình (**Lock when Helm is in the background**).

Chọn **Lock** ở đầu trang Vault để khóa ngay. Đã khóa nghĩa là khóa giải mã không còn trong bộ nhớ: kể cả một chương
trình chạy dưới tài khoản của chính mình cũng không đọc được vault trên ổ đĩa.

Mở khóa bằng **Windows Hello / vân tay** áp dụng riêng từng thiết bị. Vault vẫn hỏi mật khẩu mỗi 14 ngày (**Ask for
the password again**) và sau 5 lần mở nhanh thất bại, để không quên mật khẩu. Trên Android, thêm vân tay mới sẽ tắt
mở khóa nhanh cho đến khi gõ lại mật khẩu.

**Hide from screen capture** (bật sẵn): khi vault đang mở, cửa sổ của nó hiện màu đen khi chụp màn hình, quay màn
hình hay chia sẻ màn hình trên PC, và trống trong ảnh chụp cũng như màn hình ứng dụng gần đây trên Android.

## Sao lưu

Sync không phải là bản sao lưu: một thao tác nhầm trên một máy sẽ đồng bộ sang mọi máy. Vì vậy Vault còn giữ **bản
sao lưu mã hóa** của riêng nó trong một thư mục tự chọn.

### Cách hoạt động

- Mỗi ngày một lần (và khi chọn **Back up now**), Vault ghi một snapshot vào thư mục backup, rồi **đọc lại và kiểm
  tra**. Bản backup chỉ được tính là xong sau bước kiểm tra đó. **Backup history** hiện các lần chạy gần nhất.
- Snapshot được giữ 30 ngày (hằng ngày) và 12 tháng (hằng tháng); cả hai chỉnh được. Mỗi tài liệu chỉ lưu một lần,
  dù bao nhiêu snapshot dùng đến nó.
- Mỗi lần chạy còn kiểm tra ngẫu nhiên một phần các file cũ và sửa file bị hỏng.
- Để thư mục này trong Google Drive hay OneDrive là an toàn: không có mật khẩu vault hoặc recovery key thì nó vô
  dụng. Trong thư mục có file `README.txt` giải thích nó là gì.

Trang Vault hiện cảnh báo khi vault chưa từng được sao lưu, hoặc khi quá 7 ngày không có bản backup tốt (kèm lỗi gần
nhất, nếu có).

### Lấy lại mục đã xóa

**Settings** → **Restore deleted items** → **Restore** đưa lại các mục (và tài liệu của chúng) có trong bản backup
mới nhất nhưng không còn trong vault. Nó không bao giờ ghi đè các mục đang có.

### Khôi phục toàn bộ trên thiết bị mới

Trên một thiết bị chưa từng cài Vault (ví dụ sau khi mất hết thiết bị):

1. Mở **Vault**. Ở **Have a backup?**, gõ mật khẩu vault của bản backup, hoặc tích **Use the recovery key** rồi gõ
   key trên Emergency Kit.
2. Chọn **Restore from a backup folder…** và chọn đúng thư mục backup đã đặt trước đây: thư mục **chứa**
   `HelmVault-…`, không phải chính thư mục đó.

### Không cần Helm

Mỗi bản phát hành có **helm-vault-restore-win-x64.exe**, một chương trình duy nhất mở được thư mục backup mà không
cần Helm. Hãy cất một bản cạnh Emergency Kit.

```
helm-vault-restore list   <thư mục backup>
helm-vault-restore kdbx   <thư mục backup> <file mới.kdbx>
helm-vault-restore export <thư mục backup> <thư mục trống để xuất>
```

- `list` liệt kê các snapshot. `kdbx` chuyển bản backup thành cơ sở dữ liệu KeePass, khóa bằng một mật khẩu mới.
  `export` ghi mọi mục (`items.json`) và mọi tài liệu ở dạng **đã giải mã**: giữ thư mục đó cẩn thận và xóa khi dùng
  xong.
- Tùy chọn: `--snapshot <tên>` (mặc định: bản mới nhất), `--recovery` (dùng recovery key thay cho mật khẩu),
  `--trash` (lấy cả các mục trong thùng rác), `--vault <id>` (khi thư mục chứa nhiều vault).

### Xuất sang KeePass

**Settings** → **Export to KeePass** lưu một file KDBX mà KeePassXC, KeePass và KeePassDX mở được, khóa bằng mật khẩu
riêng (ít nhất 14 ký tự). **Include documents** thêm cả các file, nên bản xuất sẽ lớn bằng tổng tài liệu. Đây là lối
thoát nếu có ngày không dùng Helm nữa, và là một bản sao lưu không phụ thuộc vào Helm.

## Khi có sự cố

| Tình huống | Cách xử lý |
|---|---|
| **Quên mật khẩu vault** | Ở màn hình khóa, tích **I forgot the password: use the recovery key** và gõ key trên Emergency Kit. Vault sẽ yêu cầu **Set a new vault password**. Recovery key vẫn dùng tiếp được. |
| **Mất Emergency Kit** | Khi vẫn còn nhớ mật khẩu: **Settings** → **Recovery key** → gõ mật khẩu hiện tại → **Create a new recovery key**, rồi lưu kit mới. Key cũ hết hiệu lực. Kit đang cất phải có đúng **recovery key id** mà phần cài đặt hiển thị. |
| **Muốn đổi mật khẩu** | **Settings** → **Change the vault password**. Các thiết bị khác dùng mật khẩu mới sau khi đồng bộ; recovery key giữ nguyên. |
| **"Edited on two devices"** | Cả hai phiên bản đều được giữ. Xem cả hai rồi chọn **Keep this one** (hoặc **Keep the version shown here**). Bản còn lại vào lịch sử, không mất gì. |
| **"Sync is holding back deletions"** | Một thiết bị khác (hoặc ai đó có tài khoản sync) vừa xóa nhiều mục cùng lúc mà không qua thùng rác. Trên máy này chưa có gì bị xóa. Nếu đúng là mình xóa, chọn **Delete them here too**; nếu không, chọn **Keep my items (restore them everywhere)**. |
| **"… records cannot be opened with this vault's key"** | Các bản ghi này đến từ vault khác hoặc bị hỏng. Chúng được giữ lại, không bị xóa. Nếu thiếu mục, dùng **Restore deleted items** từ bản backup. |
| **Mất hoặc bị lấy cắp điện thoại, PC** | Vault trên thiết bị đó đang khóa (và tự khóa). Đổi mật khẩu vault trên một thiết bị khác, và đổi Helm Sync passphrase. Nếu nghi có người đã thấy vault lúc đang mở, đổi luôn các mật khẩu lưu trong đó. |
| **Mất hết thiết bị** | Cài Helm, cài Helm Sync, rồi mở khóa bằng mật khẩu. Nếu sync cũng không còn, làm theo [Khôi phục toàn bộ trên thiết bị mới](#khôi-phục-toàn-bộ-trên-thiết-bị-mới) hoặc dùng [helm-vault-restore](#không-cần-helm). |

## Cài đặt

Mở bằng **Settings** ở đầu trang Vault, hoặc bằng mũi tên cạnh Vault ở Home → Utilities.

| Cài đặt | Mặc định | Ghi chú |
|---|---|---|
| Lock after inactivity | 5 phút | |
| Lock when Windows locks (PC) | Bật | Cả khi đăng xuất và khi máy ngủ |
| Lock when Helm is in the background (Android) | 30 giây | |
| Unlock with Windows Hello / fingerprint or face | Tắt | Riêng từng thiết bị |
| Ask for the password again | 14 ngày | Kể cả khi dùng mở khóa nhanh |
| Clear copied secrets | 30 giây | |
| Hide from screen capture / screenshots | Bật | |
| Backup folder | Chưa có | Chọn ở ít nhất một thiết bị |
| Keep daily / monthly snapshots for | 30 ngày / 12 tháng | |

## Danh sách kiểm tra

- [ ] Mật khẩu vault là mật khẩu mình nhớ được và không dùng ở đâu khác.
- [ ] Emergency Kit đã in ra (hoặc nằm trên USB) và cất tách khỏi các thiết bị.
- [ ] Đã chọn thư mục backup, và **Backup history** có một bản backup tốt gần đây.
- [ ] Có một bản `helm-vault-restore-win-x64.exe` cất cạnh Emergency Kit.
- [ ] Thỉnh thoảng xuất sang KeePass, hoặc thử `helm-vault-restore list` trên thư mục backup, để chắc đường lui vẫn
      dùng được trước khi thật sự cần đến.
