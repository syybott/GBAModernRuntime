using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AGBModern;
using LibRecomp;

// Fresh process per mode: static hardware and mod state are never reset by private reflection.
string mode = args[0], folder = Path.GetFullPath(args[1]);
Directory.CreateDirectory(folder);
byte[] rom = new byte[0x200];
Encoding.ASCII.GetBytes("PACING TEST").CopyTo(rom, 0xA0); rom[0xB2] = 0x96;
string romPath = Path.Combine(folder, "input.gba"); File.WriteAllBytes(romPath, rom);
var samples = new List<object>();
var events = new List<string>();
var done = new ManualResetEventSlim();
int frames = 0, steps = 0, irqs = 0;
uint rng = 0x12345678;
long endCycles = 0;
var watch = new Stopwatch();
var first = Scheduler.CreateEvent(due => events.Add($"first:{due}"));
var second = Scheduler.CreateEvent(due => events.Add($"second:{due}"));
Recomp.RegisterConfigPath(folder);
Recomp.RegisterGame(new("pacing", "pacing", "PACING TEST", Convert.ToHexString(SHA1.HashData(rom)),
    SaveType.None, GPIODevices.None, ctx =>
    {
        Recomp.RegisterFunctions([(0x08000100, irq =>
        {
            irqs++;
            ushort flags = Memory.Read16(0x04000202);
            Memory.Write16(0x04000202, flags);
        })]);
        Memory.Write32(0x03007FFC, 0x08000100);
        Memory.Write16(0x04000200, 0x103B); // video, timer0/1, DMA0, keypad
        Memory.Write16(0x04000208, 1);
        Memory.Write16(0x04000004, (80 << 8) | 0x38);
        Memory.Write16(0x04000100, 0xF000); Memory.Write16(0x04000102, 0xC1);
        Memory.Write16(0x04000104, 0xFFFE); Memory.Write16(0x04000106, 0xC4);
        Memory.Write16(0x04000132, 0x4001);
        Memory.Write32(0x02001000, 0xABCD1234);
        Memory.Write32(0x040000B0, 0x02001000); Memory.Write32(0x040000B4, 0x02002000);
        Memory.Write16(0x040000B8, 1); Memory.Write16(0x040000BA, 0xE760);
        first.Schedule(1000); second.Schedule(1000);
        while (frames < 120)
        {
            steps++;
            rng = unchecked(rng * 1664525 + 1013904223);
            Scheduler.Cycles += 32;
            Recomp.HandleEvents(ctx);
        }
        endCycles = Scheduler.Cycles;
        done.Set();
    }));
Recomp.Start(new(1, 0, 0), error => throw new InvalidOperationException(error));
if (Recomp.SelectROM(romPath, "pacing") != ROMValidationError.Good) return 2;
Video.FrameFinished += _ =>
{
    // Read without changing the sampled cycle, just as host-side diagnostic observations do.
    Scheduler.RunUntimed(() => samples.Add(new
    {
        frame = ++frames, cycles = Scheduler.Cycles, steps, rng, irqs,
        flags = Memory.Read16(0x04000202), timer0 = Memory.Read16(0x04000100),
        timer1 = Memory.Read16(0x04000104), dma = Memory.Read32(0x02002000),
        input = Memory.Read16(0x04000130), line = Memory.Read16(0x04000006)
    }));
    Keypad.Pressed = frames % 2 == 0 ? Buttons.A : Buttons.None;
};
watch.Start();
bool started = mode == "default" ? Recomp.StartGame("pacing") : Recomp.StartGame("pacing", paced: mode == "paced");
if (!started || !done.Wait(TimeSpan.FromSeconds(20))) return 3;
watch.Stop(); Recomp.Quit();
var report = new { mode, frames, steps, irqs, cycles = endCycles, seconds = watch.Elapsed.TotalSeconds,
    frames_per_second = frames / watch.Elapsed.TotalSeconds, events, samples,
    state_sha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { frames, steps, irqs, endCycles, rng, events, samples }))) };
File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;
