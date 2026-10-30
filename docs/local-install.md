# Cài bản port làm lệnh `notepad` trên máy phát triển

Ghi lại cách bản port được cài trên máy phát triển ngày 2026-10-30 (Windows 11, `main` tại `240fa1e`), để lệnh
`notepad` mở `NotepadClassicPort.exe` thay cho Notepad của Windows. Đây là cấu hình của một máy, không phải trình
cài đặt: không có gì trong đó được build hay test tự động.

## Bản build

```
dotnet publish src/NotepadClassic.App -c Release -r win-x64 -o C:\tools\NotepadClassicPort
```

- Framework-dependent, ReadyToRun (`PublishReadyToRun` trong `NotepadClassic.App.csproj`), cần .NET 10 Desktop
  Runtime trên máy. 14 file, khoảng 1,8 MB.
- Thư mục con `C:\tools\NotepadClassicPort\` thay vì thẳng `C:\tools` (đã có trong PATH người dùng), để không lẫn với
  công cụ khác.
- Nếu trước đó đã publish composite ReadyToRun thì xóa `src/NotepadClassic.App/obj/Release/net10.0-windows/win-x64`
  trước (xem `performance.md`), nếu không app fail-fast lúc khởi động.
- Cập nhật: đóng mọi cửa sổ của port rồi chạy lại lệnh trên (file đang chạy bị khóa).

## Vì sao không chỉ đặt `notepad.exe` vào PATH

`C:\Windows\system32` và `C:\Windows` nằm trong PATH máy, luôn đứng trước PATH người dùng, nên một `notepad.exe`
khác trong PATH không bao giờ được tìm thấy trước. Mỗi nơi gõ lệnh có cơ chế riêng, nên cấu hình theo từng nơi.

## Những gì đã đổi

| Nơi gõ `notepad` | Cơ chế | Thay đổi |
|---|---|---|
| PowerShell 7 | Hàm được ưu tiên hơn file thực thi trong PATH | Thêm vào cuối `$PROFILE` (bản sao lưu `$PROFILE.bak-notepad`): `function notepad { & 'C:\tools\NotepadClassicPort\NotepadClassicPort.exe' @args }` |
| cmd | Macro doskey nạp khi cmd khởi động | File `C:\tools\NotepadClassicPort\cmd-macros.doskey` chứa `notepad="C:\tools\NotepadClassicPort\NotepadClassicPort.exe" $*`; giá trị `AutoRun` (REG_EXPAND_SZ) trong `HKCU\Software\Microsoft\Command Processor` = `doskey /macrofile="C:\tools\NotepadClassicPort\cmd-macros.doskey"` (trước đó không có giá trị này) |
| Win+R, `ShellExecute("notepad")` | `HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\notepad.exe` | `(Default)` = `C:\tools\NotepadClassicPort\NotepadClassicPort.exe`, `Path` = `C:\tools\NotepadClassicPort`. Trước đó khóa trỏ tới Notepad bản Store (`Microsoft.WindowsNotepad`), bản sao lưu ở `C:\tools\NotepadClassicPort\apppaths-notepad-backup.reg` |

Không đổi gì trong HKLM. Khóa `Image File Execution Options\notepad.exe` (bộ lọc `FilterFullPath` chuyển
`System32\notepad.exe` sang Notepad bản Store) vẫn như cũ, nên chương trình nào gọi thẳng đường dẫn đầy đủ của
Notepad Windows vẫn mở Notepad Windows. `notepad.exe` (có đuôi) trong PowerShell vẫn gọi bản của Windows.

## Đã kiểm tra

- `pwsh`: `(Get-Command notepad).CommandType` là `Function`; cmd: `doskey /macros` in ra macro trên.
- `ShellExecute("notepad")` (cùng đường với hộp Run): trước khi đổi mở Notepad bản Store, sau khi đổi mở
  `NotepadClassicPort.exe`; với tham số là file có khoảng trắng, tiêu đề là `thu nghiem.txt - Notepad Classic Port`.
- Win+R `notepad` bấm tay: mở bản port (người dùng xác nhận).

## Chưa đổi

- Bấm đúp file `.txt` trong Explorer vẫn mở Notepad bản Store: liên kết loại file (`UserChoice`) có hash, Windows
  không cho chương trình tự ghi. Đổi bằng tay: chuột phải file `.txt`, Open with, Choose another app, duyệt tới
  `C:\tools\NotepadClassicPort\NotepadClassicPort.exe`, chọn Always.
- Khi Notepad bản Store cập nhật, nó có thể ghi lại khóa `App Paths`. Khi Win+R lại mở bản Store, chạy lại:

  ```
  reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\notepad.exe" /ve /d "C:\tools\NotepadClassicPort\NotepadClassicPort.exe" /f
  reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\notepad.exe" /v Path /d "C:\tools\NotepadClassicPort" /f
  ```

## Gỡ bỏ

```
Copy-Item "$PROFILE.bak-notepad" $PROFILE -Force
reg delete "HKCU\Software\Microsoft\Command Processor" /v AutoRun /f
reg import C:\tools\NotepadClassicPort\apppaths-notepad-backup.reg
```

Sau đó mới xóa `C:\tools\NotepadClassicPort\` (bản sao lưu `App Paths` nằm trong đó). Settings của port
(`HKCU\Software\NotepadClassicPort`) và thư mục autosave (`%LOCALAPPDATA%\NotepadClassicPort`) không bị đụng.
