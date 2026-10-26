// cuepilot-audio: the macOS audio source for DesktopCapture.
//
//   cuepilot-audio system   system audio via ScreenCaptureKit (macOS 13+)
//   cuepilot-audio mic      the default microphone via AVAudioEngine
//   cuepilot-audio request-mic   asks for microphone access (the macOS prompt) and exits: 0 allowed, 2 not
//   cuepilot-audio decode <media> <out.wav>   the sound of an audio or video file (mp3, m4a, wav, aiff, mp4, mov…)
//                                             as a 16 kHz mono 16-bit WAV, via AVFoundation; exit 4 = no sound track
//
// Writes 48 kHz mono 32-bit float PCM to stdout and stops when stdin closes.
// Exit codes: 0 stopped, 1 error, 2 permission denied, 3 macOS too old, 64 usage.
// Runs inside CuePilot.app, so macOS asks for the permissions on behalf of CuePilot.

import AVFoundation
import CoreGraphics
import CoreMedia
import Foundation
import ScreenCaptureKit

func log(_ text: String) {
    FileHandle.standardError.write((text + "\n").data(using: .utf8)!)
}

/// Converts any incoming buffer to 48 kHz mono float32 and writes it to stdout.
final class PcmWriter {
    private let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48000, channels: 1, interleaved: false)!
    private var converter: AVAudioConverter?
    private var converterInput: AVAudioFormat?
    private let lock = NSLock()

    func write(_ buffer: AVAudioPCMBuffer) {
        lock.lock()
        defer { lock.unlock() }
        if buffer.format == target {
            emit(buffer)
            return
        }

        if converter == nil || converterInput != buffer.format {
            converter = AVAudioConverter(from: buffer.format, to: target)
            converter?.downmix = true
            converterInput = buffer.format
        }

        guard let converter else {
            log("cannot convert from \(buffer.format)")
            return
        }

        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * target.sampleRate / buffer.format.sampleRate) + 64
        guard let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else {
            return
        }

        var consumed = false
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, inputStatus in
            if consumed {
                inputStatus.pointee = .noDataNow
                return nil
            }

            consumed = true
            inputStatus.pointee = .haveData
            return buffer
        }

        if status == .error {
            log("conversion failed: \(error?.localizedDescription ?? "unknown")")
            return
        }

        emit(output)
    }

    private func emit(_ buffer: AVAudioPCMBuffer) {
        guard buffer.frameLength > 0, let samples = buffer.floatChannelData?[0] else {
            return
        }

        var pointer = UnsafeRawPointer(samples)
        var remaining = Int(buffer.frameLength) * MemoryLayout<Float>.size
        while remaining > 0 {
            let written = Darwin.write(STDOUT_FILENO, pointer, remaining)
            if written < 0 {
                if errno == EINTR {
                    continue
                }

                // DesktopCapture is gone: nothing left to record for.
                exit(0)
            }

            pointer = pointer.advanced(by: written)
            remaining -= written
        }
    }
}

let writer = PcmWriter()

@available(macOS 13.0, *)
final class SystemAudioCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    private var stream: SCStream?
    private let queue = DispatchQueue(label: "cuepilot.audio.system")

    func start() async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else {
            throw NSError(domain: "cuepilot-audio", code: 1, userInfo: [NSLocalizedDescriptionKey: "no display found"])
        }

        // ScreenCaptureKit always captures a display; the picture is kept tiny and rare, only the audio is used.
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        let configuration = SCStreamConfiguration()
        configuration.capturesAudio = true
        configuration.excludesCurrentProcessAudio = true
        configuration.sampleRate = 48000
        configuration.channelCount = 1
        configuration.width = 2
        configuration.height = 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)

        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
        try await stream.startCapture()
        self.stream = stream
        log("system audio capture started")
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sampleBuffer.isValid,
              let description = CMSampleBufferGetFormatDescription(sampleBuffer) else {
            return
        }

        let format = AVAudioFormat(cmAudioFormatDescription: description)
        let frames = AVAudioFrameCount(CMSampleBufferGetNumSamples(sampleBuffer))
        guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else {
            return
        }

        buffer.frameLength = frames
        let status = CMSampleBufferCopyPCMDataIntoAudioBufferList(
            sampleBuffer, at: 0, frameCount: Int32(frames), into: buffer.mutableAudioBufferList)
        if status == noErr {
            writer.write(buffer)
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        log("system audio capture stopped: \(error.localizedDescription)")
        exit(1)
    }
}

final class MicrophoneCapture {
    private let engine = AVAudioEngine()

    func start() throws {
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else {
            throw NSError(domain: "cuepilot-audio", code: 1, userInfo: [NSLocalizedDescriptionKey: "no microphone found"])
        }

        input.installTap(onBus: 0, bufferSize: 4800, format: format) { buffer, _ in
            writer.write(buffer)
        }
        engine.prepare()
        try engine.start()
        log("microphone capture started: \(format)")
    }
}

/// Keeps the capture objects alive for the life of the process.
var captureHolder: AnyObject?

func startSystem() {
    guard #available(macOS 13.0, *) else {
        log("system audio capture needs macOS 13 or newer")
        exit(3)
    }

    // Without the permission ScreenCaptureKit fails; the request makes macOS list CuePilot in System Settings.
    if !CGPreflightScreenCaptureAccess() {
        CGRequestScreenCaptureAccess()
        log("screen and system audio recording is not allowed")
        exit(2)
    }

    let capture = SystemAudioCapture()
    captureHolder = capture
    Task {
        do {
            try await capture.start()
        } catch {
            log("system audio capture failed: \(error.localizedDescription)")
            exit(CGPreflightScreenCaptureAccess() ? 1 : 2)
        }
    }
}

func startMicrophone() {
    let begin = {
        let capture = MicrophoneCapture()
        captureHolder = capture
        do {
            try capture.start()
        } catch {
            log("microphone capture failed: \(error.localizedDescription)")
            exit(1)
        }
    }

    switch AVCaptureDevice.authorizationStatus(for: .audio) {
    case .authorized:
        begin()
    case .notDetermined:
        AVCaptureDevice.requestAccess(for: .audio) { granted in
            DispatchQueue.main.async {
                if granted {
                    begin()
                } else {
                    log("microphone access is not allowed")
                    exit(2)
                }
            }
        }
    default:
        log("microphone access is not allowed")
        exit(2)
    }
}

func requestMicrophone() {
    switch AVCaptureDevice.authorizationStatus(for: .audio) {
    case .authorized:
        exit(0)
    case .notDetermined:
        AVCaptureDevice.requestAccess(for: .audio) { granted in
            exit(granted ? 0 : 2)
        }
    default:
        exit(2)
    }
}

/// RIFF header of a 16 kHz mono 16-bit PCM WAV with the given data size.
func wavHeader(dataBytes: UInt32) -> Data {
    var header = Data()
    func append32(_ value: UInt32) { withUnsafeBytes(of: value.littleEndian) { header.append(contentsOf: $0) } }
    func append16(_ value: UInt16) { withUnsafeBytes(of: value.littleEndian) { header.append(contentsOf: $0) } }
    header.append(contentsOf: Array("RIFF".utf8))
    append32(36 + dataBytes)
    header.append(contentsOf: Array("WAVEfmt ".utf8))
    append32(16)
    append16(1)       // PCM
    append16(1)       // mono
    append32(16000)   // sample rate
    append32(32000)   // bytes per second
    append16(2)       // block align
    append16(16)      // bits per sample
    header.append(contentsOf: Array("data".utf8))
    append32(dataBytes)
    return header
}

func decodeFile(input: String, output: String) {
    Task {
        do {
            let asset = AVURLAsset(url: URL(fileURLWithPath: input))
            guard let track = try await asset.loadTracks(withMediaType: .audio).first else {
                log("the file has no sound track")
                exit(4)
            }

            let reader = try AVAssetReader(asset: asset)
            let settings: [String: Any] = [
                AVFormatIDKey: kAudioFormatLinearPCM,
                AVSampleRateKey: 16000,
                AVNumberOfChannelsKey: 1,
                AVLinearPCMBitDepthKey: 16,
                AVLinearPCMIsFloatKey: false,
                AVLinearPCMIsBigEndianKey: false,
                AVLinearPCMIsNonInterleaved: false
            ]
            let trackOutput = AVAssetReaderTrackOutput(track: track, outputSettings: settings)
            trackOutput.alwaysCopiesSampleData = false
            reader.add(trackOutput)
            guard reader.startReading() else {
                throw reader.error ?? NSError(domain: "cuepilot-audio", code: 1, userInfo: [NSLocalizedDescriptionKey: "cannot read the file"])
            }

            guard FileManager.default.createFile(atPath: output, contents: wavHeader(dataBytes: 0)),
                  let file = FileHandle(forWritingAtPath: output) else {
                throw NSError(domain: "cuepilot-audio", code: 1, userInfo: [NSLocalizedDescriptionKey: "cannot create \(output)"])
            }

            file.seekToEndOfFile()
            var total: UInt64 = 0
            while let sampleBuffer = trackOutput.copyNextSampleBuffer() {
                guard let block = CMSampleBufferGetDataBuffer(sampleBuffer) else {
                    continue
                }

                let length = CMBlockBufferGetDataLength(block)
                var data = Data(count: length)
                let status = data.withUnsafeMutableBytes { bytes in
                    CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: bytes.baseAddress!)
                }
                if status == kCMBlockBufferNoErr {
                    file.write(data)
                    total += UInt64(length)
                }
            }

            if reader.status == .failed {
                throw reader.error ?? NSError(domain: "cuepilot-audio", code: 1, userInfo: [NSLocalizedDescriptionKey: "decoding failed"])
            }

            file.seek(toFileOffset: 0)
            file.write(wavHeader(dataBytes: UInt32(min(total, UInt64(UInt32.max - 36)))))
            file.closeFile()
            exit(0)
        } catch {
            log("decoding failed: \(error.localizedDescription)")
            exit(1)
        }
    }
}

signal(SIGPIPE, SIG_IGN)
let arguments = CommandLine.arguments
guard arguments.count >= 2 else {
    log("usage: cuepilot-audio system|mic|request-mic")
    exit(64)
}

if arguments[1] == "request-mic" {
    requestMicrophone()
    dispatchMain()
}

if arguments[1] == "decode" {
    guard arguments.count >= 4 else {
        log("usage: cuepilot-audio decode <media file> <output.wav>")
        exit(64)
    }

    decodeFile(input: arguments[2], output: arguments[3])
    dispatchMain()
}

// DesktopCapture closes stdin to stop the stream.
Thread.detachNewThread {
    while readLine(strippingNewline: true) != nil {}
    exit(0)
}

switch arguments[1] {
case "system":
    startSystem()
case "mic":
    startMicrophone()
default:
    log("usage: cuepilot-audio system|mic|request-mic")
    exit(64)
}

dispatchMain()
