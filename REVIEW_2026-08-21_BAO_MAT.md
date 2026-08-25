# Rà soát bảo mật HinoGateway

> ## ⚠ TRẠNG THÁI FILE NÀY
>
> - **Ngày:** 2026-08-21
> - **Loại:** rà soát bảo mật toàn bộ `src/`. Các bản review trước (`REVIEW_2026-08-17`,
>   `REVIEW_2026-08-18_DOI_CHIEU`) tập trung vào độ ổn định và chịu tải; file này chỉ nhìn mặt bảo mật.
> - **✅ ĐÃ SỬA XONG trong cùng ngày.** Thân bài mô tả mã nguồn **trước khi sửa** — đọc như hồ sơ
>   chẩn đoán, không phải hiện trạng.
> - **525 test PASS** (433 unit + 92 API), tăng từ mốc 491. Build Release 0 warning, 0 error.
> - Đã kiểm chứng bằng cách **chạy gateway thật** và tấn công nó, không chỉ chạy test — xem §5.
> - §4 liệt kê những thứ **đã kiểm và không cần sửa**, để lần rà sau khỏi làm lại.

---

## 1. Kết luận chung

Kiến trúc bảo mật của gateway **tốt hơn mức thường thấy ở phần mềm loại này**. Những thứ đã đúng sẵn,
và đúng vì có người nghĩ chứ không phải nhờ may:

- Ma trận phân quyền `Viewer` / `Operator` / `Admin` phủ **mọi** controller, có
  `AuthorizationMatrixTests` khoá lại từng route.
- Bí mật tách khỏi project file bằng tham chiếu `${secret:…}` / `${env:…}`, file secret có ACL riêng,
  và `ProtectDevice`/`ProtectMqtt` được gọi **cả** ở đường `PUT` lẫn `POST /api/config/import` — nên
  một file `.hino` mang mật khẩu dạng literal sẽ bị hút vào secret store thay vì lưu nguyên.
- Forwarded-headers **fail-closed**: không tin proxy nào theo mặc định, kể cả loopback.
- IEC-104 có allowlist IP **fail-closed** (`AllowedClients` mặc định chỉ loopback), khớp CIDR đúng,
  có `MaxConnections`, `AllowCommands`, và hàng đợi lệnh có trần.
- So sánh API key bằng `CryptographicOperations.FixedTimeEquals`; khoá sai không bị ghi ra log.
- Lệnh ghi PLC có rate limit riêng + audit bền, kể cả lệnh bị từ chối.
- Không có XSS (`MarkupString` không xuất hiện ở đâu), không SQL, không `BinaryFormatter`, không
  `TypeNameHandling`, không chỗ nào tắt kiểm tra chứng thư TLS.

Nhưng có **một lỗ P0 giết được tiến trình từ xa** và một số lỗ hạng trung. Tất cả đã vá.

---

## 2. P0 — Tràn stack giết tiến trình, gọi được từ xa ở mức Viewer

**Vị trí:** `src/HinoGateway.Expressions/{Parser,Lexer,ExpressionCompiler}.cs`,
`src/HinoGateway.Server/Controllers/ExpressionsController.cs`.

`Parser` là recursive-descent **không có giới hạn độ sâu**; `Nodes.Evaluate` cũng đệ quy; `Lexer`
không giới hạn độ dài; `ExpressionCompiler.Compile` không giới hạn gì. `POST /api/expressions/validate`
và `/evaluate` chỉ yêu cầu `Viewer` — mà với mặc định `Security:Enabled=false`, **mọi** người gọi
đều là Viewer.

Hai đường khai thác, và đường thứ hai nặng hơn:

1. **Sập ngay.** Body ~10 KB gồm 5000 dấu `(` lồng nhau. Mỗi cấp `(` tốn ~11 stack frame trên đường
   đi xuống thang ưu tiên. Vượt stack 1 MB → `StackOverflowException`, mà .NET **không cho bắt** —
   tiến trình chết ngay, mang theo mọi vòng poll và cả hai đường northbound.
   `GatewaySupervisorService` không cứu được; service khởi động lại rồi kẻ tấn công gửi tiếp.
2. **Làm hỏng luôn cấu hình.** Một biểu thức *hợp lệ* nhưng dài (`1+1+1+…`) parse được ở độ sâu
   **hằng số**, vì `ParseAdditive` và các cấp lân cận dùng vòng `while` chứ không đệ quy — nhưng nó
   dựng một cây lệch trái cao bằng số hạng. Không có gì tràn lúc parse; tràn xảy ra **lúc `Evaluate`**,
   mỗi vòng poll một lần. Lưu nó thành tag `Expression` trong `hino.json` thì gateway sập cả lúc khởi
   động, và **chỉ sửa tay file JSON mới gỡ được**.

Để đối chiếu: biểu thức thật dài nhất trong `EVNRIKEN_Project.hino` là **99 ký tự**.

**Đã vá** bằng bốn trần, cố ý đặt rộng gấp hàng chục lần nhu cầu thật:

| Trần | Giá trị | Chặn cái gì |
| --- | --- | --- |
| `ExpressionCompiler.MaxExpressionLength` | 4096 ký tự | trần ngoài, rẻ nhất |
| `Lexer.MaxTokens` | 2048 token | chặn trước khi có cây |
| `Parser.MaxDepth` | 64 cấp | đệ quy lúc parse (`((((…`) |
| `Parser.MaxNodes` | 1024 node | **chiều cao cây** → đệ quy lúc `Evaluate` |

`MaxDepth` và `MaxNodes` **không thay thế được cho nhau**, và thiếu một trong hai là còn nguyên lỗ:
`MaxDepth` không thấy chuỗi phẳng, `MaxNodes` không thấy độ sâu rẻ tiền. Có test riêng cho từng cái,
kèm một test khẳng định biểu thức 99 ký tự thật vẫn compile.

Cả bốn đều ném `ExpressionSyntaxException` (đã có sẵn, mang vị trí ký tự), nên trình soạn công thức
hiện lỗi bình thường — không có đường nào chui ra thành HTTP 500.

---

## 3. Các phát hiện còn lại

### 3.1 DNS rebinding → Admin → ghi PLC (đã vá)

**Vị trí:** `Security/GatewaySecurity.cs`, `GatewayApiKeyHandler.IsLocalRequest`.

Mặc định: Kestrel nghe `http://localhost:2000`, `Security:Enabled=false`, `AllowLocalConsole=true`
⇒ mọi request từ loopback được **Admin**. `AllowedHosts` là `"*"` nên host-filtering tắt.

Kịch bản: kỹ sư mở trình duyệt trên IPC vào một trang độc; trang đó cho tên miền của nó phân giải về
`127.0.0.1`. Request tới `http://tên-của-nó:2000/api/tags/write` là **same-origin** với trang đó →
không preflight, không CORS, không cần khoá — và đến Kestrel từ loopback y hệt console thật.
Địa chỉ nguồn nói thật mà vẫn là thứ sai để tin.

Đây đúng là cấu hình mà `HUONG_DAN_TRIEN_KHAI_IPC.md` §7.3 phương án A khuyến nghị cho IPC một máy.

**Đã vá** bằng điều kiện thứ ba trong `IsLocalRequest`: header `Host` phải là `localhost` /
`127.0.0.0/8` / `::1`. Console thật luôn thoả; trang bị rebind thì không, vì `Host` của nó là tên
miền của nó. Chọn cách này thay vì siết `AllowedHosts` để **không phá cấu hình đang chạy** — ai truy
cập bằng hostname hay IP LAN vẫn vào được, chỉ là với quyền `Viewer` hoặc bằng API key, đúng như
trước.

> **Thay đổi hành vi cần biết:** mở giao diện bằng `http://TÊN-MÁY:2000` **ngay trên IPC** giờ chỉ
> được `Viewer`. Dùng `http://localhost:2000`. Đã ghi vào §7.3 của hướng dẫn triển khai.

### 3.2 POST chỉ-đọc ép ghi audit đồng bộ xuống đĩa (đã vá)

**Vị trí:** `Services/AuditMiddleware.cs`.

`AuditTrail.WriteAsync` giữ người gọi cho tới khi batch **đã flush xuống đĩa** (`FileOptions.WriteThrough`
+ `Flush(flushToDisk: true)`), qua một channel bounded ở chế độ `Wait`. Đúng cho một lệnh ghi PLC.
Sai cho `/api/expressions/validate` và `/evaluate` — hai endpoint chỉ là POST vì công thức không nhét
vừa query string, chúng compile một đoạn text rồi trả lời, không lưu gì và không ghi thiết bị nào.

Hệ quả: mỗi phím gõ trong trình soạn công thức là một lần flush đĩa; và vì cả hai ở mức `Viewer`, một
gateway không đặt khoá trên LAN nhà máy cho bất kỳ ai gõ được cổng một cách để **đẩy đĩa chạy hết
sức** và bơm đầy audit tới trần 2 GB bằng những sự kiện không phải sự kiện.

**Đã vá**: `AuditMiddleware` bỏ qua `/api/expressions`.

> **Chẩn đoán ban đầu của rà soát này đã sai và được sửa lại bằng cách chạy thử.** Giả thuyết đầu là
> "request 401/403 cũng bị audit". **Không đúng**: `UseAuthorization` nằm **trước** `AuditMiddleware`
> trong pipeline và short-circuit, nên 401/403 không bao giờ tới middleware này. Đã kiểm bằng một test
> thăm dò in ra đúng những dòng audit thật sự được ghi. Bài học giữ lại: thứ tự middleware của
> ASP.NET Core không suy ra được từ thứ tự dòng code trong `Program.cs`, vì `WebApplication` tự chèn
> `UseRouting`/`UseEndpoints`.

### 3.3 Né được throttle chống dò khoá (đã vá)

`GatewayApiKeyHandler` gọi `Task.Delay(delay, Context.RequestAborted)`. Kẻ dò khoá **không chờ câu
trả lời** — gửi rồi cúp — nên chỉ cần đóng kết nối là bỏ qua toàn bộ độ trễ, và mỗi lần như vậy ném
một `TaskCanceledException` ra khỏi authentication handler. Đã bỏ cancellation token.

### 3.4 Thiếu security header (đã vá)

Không có `Content-Security-Policy`, `X-Content-Type-Options`, `Referrer-Policy`. Cái quan trọng nhất
là **`frame-ancestors 'none'`**: request từ chính máy đó được cấp Admin khi chưa đặt khoá, nên một
trang bất kỳ nhúng giao diện này vào iframe vô hình rồi dụ người vận hành bấm xuyên qua là một trang
**dời được setpoint**.

Chính sách đã đặt trong `Program.cs`. Hai chỗ nới là của Blazor WebAssembly chứ không phải của mình:
`'wasm-unsafe-eval'` là cách .NET runtime khởi tạo, và `'unsafe-inline'` trong `style-src` là do
MudBlazor ghi style inline. Cả hai giới hạn trong directive của chúng, không nới `default-src`.

### 3.5 Static file phục vụ trước cổng HTTPS (đã vá)

`UseBlazorFrameworkFiles()` / `UseStaticFiles()` nằm **trên** khối `RequireHttps`, nên toàn bộ client
app vẫn được phục vụ qua HTTP thuần ngay cả khi đã bật security. Đã đảo thứ tự.

### 3.6 Không giới hạn nhịp request ở mức toàn cục (đã vá)

Rate limiter chỉ gắn cho `/api/tags/write`. Đã thêm `GlobalLimiter` 200 req/s mỗi caller (phân vùng
theo identity rồi tới IP, `RateLimit:RequestsPerSecond` chỉnh được), miễn trừ `/health` để không biến
một phút bận thành một lần restart. Xô `commands` 20/s cho lệnh PLC giữ nguyên.

Chọn global limiter thay vì gắn `[EnableRateLimiting("commands")]` lên từng controller như dự định
ban đầu, vì cách đó sẽ bóp cả các GET mà giao diện đang poll **chung một xô 20/s với lệnh ghi PLC**.

### 3.7 API key đi trong query string của SignalR (đã có tài liệu, không sửa được bằng code)

WebSocket trong trình duyệt **không đặt được header tuỳ ý**, nên SignalR truyền khoá qua
`?access_token=`. Gateway không tự làm rò: `AuditMiddleware` chỉ ghi `Request.Path` (không query) và
`UseSerilogRequestLogging` không bật. Nhưng **reverse proxy đứng trước sẽ ghi khoá vào access log**.
Đã có sẵn trong hướng dẫn triển khai §7.3 (cấu hình nginx dùng `$uri` chứ không `$request_uri`).

### 3.8 Thư mục cài đặt không được siết ACL (đã ghi vào checklist nghiệm thu)

`DriverPlugins` nạp mọi `.dll` trong `drivers/` lúc khởi động, **không ký số** — ranh giới tin cậy này
được ghi rõ và chấp nhận có ý thức. Nhưng gateway chỉ tự siết ACL **thư mục dữ liệu**
(`GatewayDataPaths.HardenRootPermissions`); thư mục **cài đặt** thì không, vì nó do người cài tạo ra.
Cài vào một đường dẫn tự tạo dưới `C:\` mà ai cũng ghi được ⇒ thả một DLL vào `drivers/` = chạy mã
dưới quyền service account (thường `LocalSystem`).

Không sửa bằng code (gateway không nên tự viết lại ACL thư mục chứa chính nó). Đã thêm **bước 17b**
vào checklist nghiệm thu: `icacls` thư mục cài đặt.

### 3.9 Gói phụ thuộc ghim cứng bản vá (đã ghi vào quy trình)

`Microsoft.AspNetCore.Components.WebAssembly*` ghim `8.0.15` trong khi các gói khác thả `8.0.*`, nên
chúng **không** tự lên theo bản vá của .NET. Đã thêm `dotnet list package --vulnerable --include-transitive`
vào §3 của hướng dẫn triển khai như một điều kiện đi tiếp.

---

## 4. Đã kiểm và **không** cần sửa

Ghi lại để lần rà sau khỏi làm lại từ đầu.

| Hạng mục | Kết luận |
| --- | --- |
| Phân quyền từng endpoint | Đủ. Mọi controller có `[Authorize]`; `AuthorizationMatrixTests` khoá lại |
| Mật khẩu literal trong `.hino` | Đã xử lý. `ProtectDevice`/`ProtectMqtt` chạy ở **cả** `PUT` lẫn `import`; export chỉ ghi ra tham chiếu |
| ACL file secret | Đúng. `hino.secrets.json` và cả `.bak` đều được siết, Windows ACL + Unix mode |
| CORS | Không cấu hình ⇒ trình duyệt chặn cross-origin theo mặc định. Đúng |
| So sánh API key | `FixedTimeEquals`, không rò khoá qua thời gian; khoá bị từ chối không vào log |
| IEC-104 | Allowlist fail-closed, CIDR khớp đúng, có trần kết nối và trần lệnh |
| MQTT TLS | `UseTls()` giữ nguyên kiểm tra chứng thư mặc định của MQTTnet, không có chỗ nào bypass |
| Path traversal | `/api/history` lọc theo tên tag trên **nội dung** file, không ghép vào đường dẫn |
| XSS / deserialization | Không có `MarkupString`, `BinaryFormatter`, `TypeNameHandling`, `Process.Start` |
| Health endpoint | `AllowAnonymous` nhưng dùng writer mặc định, chỉ trả `Healthy`/`Unhealthy` |
| Ghi PLC | `Operator` + rate limit + audit cả lệnh bị từ chối |

---

## 5. Kiểm chứng

**Test:** 525 PASS (433 unit + 92 API), từ mốc 491. Test mới:

- `ExpressionTests` — 4 test cho các trần, trong đó một test tồn tại **riêng** để chứng minh
  `MaxNodes` bắt được ca mà `MaxDepth` không bắt được.
- `GatewaySecurityTests` — bảng tên host được coi là console.
- `LocalConsoleTests` — 8 test DNS rebinding (3 tên lạ bị chặn, 5 cách viết tên máy vẫn qua).
- `AuthorizationMatrixTests` — kiểm tra công thức không sinh dòng audit, và thay đổi cấu hình thì có.

**Chạy thật** (`dotnet run`, gateway sống, cấu hình dev thật):

| Tấn công | Kết quả |
| --- | --- |
| 5000 dấu `(` lồng nhau → `/validate` | HTTP 200, `valid:false`, "expression is 10001 characters long" |
| 5000 số hạng `1+1+…` → `/validate` | HTTP 200, `valid:false`, "more than 2048 tokens" |
| 200 dấu `(` (dưới trần độ dài) | "nests more than 64 levels deep" |
| 600 số hạng (dưới trần token) | "built from more than 1024 parts" — **đúng trần node** |
| 400 số hạng | tính ra `400`, đúng. Trần không chặn nhầm việc thật |
| Cả bốn payload qua `/evaluate` | HTTP 400, không sập |
| **`/health/live` sau tất cả** | **200 — tiến trình còn sống** |
| `POST /api/tags/write`, `Host: localhost` | **404** (vào được route, tag không tồn tại) = còn Admin |
| `POST /api/tags/write`, `Host: evil.example` | **403** = rebinding bị chặn |
| `GET /api/tags`, `Host: evil.example` | **200** = vẫn đọc được, đúng thiết kế |

**Giao diện** (Chrome, 1920×1080, cả light lẫn dark theme): Live Data render đủ, badge LIVE và
"39 updates received" → SignalR nối được qua CSP; Tag Database và form tạo tag (MudBlazor select,
toggle, dialog) render đúng ở dark theme. **Console không có một vi phạm CSP nào.**

---

## 6. Một lỗi có sẵn phát hiện lúc kiểm chứng — **chưa sửa**

Giao diện hiện cảnh báo `audit-trail: No heartbeat for 57 seconds.` sau khi thực hiện một lệnh ghi.

Nguyên nhân: `"audit-trail"` chỉ được `Heartbeat()` trong `ControlCommandService.TryAuditAsync`, tức
**chỉ khi có lệnh điều khiển**. `ComponentHealthRegistry` đăng ký component lần đầu nó được nhắc tới,
rồi coi là `stale` sau `Health:Components:HeartbeatTimeoutSeconds` (mặc định 30 s). Nên: gateway nào
đã từng nhận một lệnh ghi và sau đó nghỉ 30 giây sẽ **báo động giả vĩnh viễn**. Trạm có lệnh liên tục
thì không thấy; trạm ít thao tác thì thấy suốt.

Không liên quan tới đợt sửa này (không đụng `ControlCommandService` hay `ComponentHealthRegistry`) —
chỉ tình cờ lộ ra vì `curl` test có gửi một lệnh ghi. Hai hướng: hoặc cho `audit-trail` nhịp định kỳ
như `audit-writer` đang làm, hoặc bỏ hẳn nó và để `audit-writer` đại diện. **Cần quyết định, chưa làm.**

---

## 7. File đã đổi

```
src/HinoGateway.Expressions/ExpressionCompiler.cs   MaxExpressionLength
src/HinoGateway.Expressions/Lexer.cs                MaxTokens
src/HinoGateway.Expressions/Parser.cs               MaxDepth + MaxNodes
src/HinoGateway.Server/Program.cs                   GlobalLimiter, security header, đảo thứ tự HTTPS/static
src/HinoGateway.Server/Security/GatewaySecurity.cs  IsLoopbackHost, bỏ RequestAborted khỏi Task.Delay
src/HinoGateway.Server/Services/AuditMiddleware.cs  WorthAuditing
tests/HinoGateway.Engine.Tests/ExpressionTests.cs
tests/HinoGateway.Engine.Tests/GatewaySecurityTests.cs
tests/HinoGateway.Api.Tests/LocalConsoleTests.cs
tests/HinoGateway.Api.Tests/AuthorizationMatrixTests.cs
HUONG_DAN_TRIEN_KHAI_IPC.md                         §3 quét gói, §7.3 quy tắc Host, bước 17b ACL
```
