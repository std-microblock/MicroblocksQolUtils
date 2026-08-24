# Local compatibility patch

This directory vendors `CapSoftware/scap` at commit
`c03f15a4631f40cd8d2e36807befb83b314cfeb7` (MIT).

The vendored source resolves `windows-capture` 1.5, where the capture-frame
timestamp accessor was renamed. The local patch uses `Frame::timestamp()` in
place of the removed `Frame::timespan()` method. Its value is a WinRT
`TimeSpan` in 100 ns units, so the Windows backend now anchors the first frame
to `SystemTime` and converts later deltas directly instead of incorrectly
dividing them by the machine's QPC frequency.

The Windows BGRA path also requests a no-padding CPU buffer. `scap` does not
expose row pitch on `BGRAFrame`, so returning the raw padded D3D staging buffer
would make consumers interpret padding bytes as the next scanline. This is an
intentional CPU copy; the recorder's memory bound comes from its fixed frame
queue rather than a zero-copy requirement.

`Window` is re-exported so an in-process client can construct a capture target
from its already-known HWND. `windows-capture` intentionally omits windows
owned by the enumerating process, but an Everest mod runs inside Celeste and
therefore cannot find the game window through `get_all_targets()`.

The capturer exposes a timeout-based frame read. Upstream's blocking
`get_next_frame()` cannot observe a recorder stop request while a window is
static or minimized, so the mod polls at 100 ms and shuts down deterministically.

The Linux backend keeps capture/error state per `LinuxCapturer` instead of in
process-wide statics, allowing the full-recording and death-replay sessions to run
independently. PipeWire rows are copied using the negotiated stride and converted
from their negotiated RGB layout by the native recorder instead of treating a
padded `spa_buffer` as tightly packed pixels.
