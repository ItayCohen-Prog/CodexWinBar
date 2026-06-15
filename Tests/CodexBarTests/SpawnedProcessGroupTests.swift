import Foundation
import Testing
@testable import CodexBarCore
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

struct SpawnedProcessGroupTests {
    @Test
    func `pipe cleanup preserves standard descriptors`() {
        let descriptors = SpawnedProcessGroup.pipeDescriptorsToClose([0, 1, 2, 3, 4, 3])

        #expect(descriptors == [3, 4])
    }

    @Test
    func `launch captures child output`() async throws {
        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        let stdoutCapture = ProcessPipeCapture(pipe: stdoutPipe)
        let stderrCapture = ProcessPipeCapture(pipe: stderrPipe)
        stdoutCapture.start()
        stderrCapture.start()

        let process = try SpawnedProcessGroup.launch(
            binary: "/bin/sh",
            arguments: ["-c", "printf stdout-value; printf stderr-value >&2"],
            environment: ProcessInfo.processInfo.environment,
            stdoutPipe: stdoutPipe,
            stderrPipe: stderrPipe)
        while process.isRunning {
            try await Task.sleep(for: .milliseconds(20))
        }
        await process.terminateResidualGroup()

        async let stdout = stdoutCapture.finish(timeout: .seconds(1))
        async let stderr = stderrCapture.finish(timeout: .seconds(1))
        let output = await (stdout, stderr)

        #expect(process.terminationStatus == 0)
        #expect(String(data: output.0, encoding: .utf8) == "stdout-value")
        #expect(String(data: output.1, encoding: .utf8) == "stderr-value")
    }

    @Test
    func `termination waits for grace before killing escaped descendants`() async throws {
        let childPIDFile = FileManager.default.temporaryDirectory
            .appendingPathComponent("codexbar-process-group-\(UUID().uuidString).pid")
        defer { try? FileManager.default.removeItem(at: childPIDFile) }

        let script = """
        import subprocess
        import sys
        import time

        child = subprocess.Popen(
            [
                sys.executable,
                "-c",
                "import signal,time; signal.signal(signal.SIGTERM, signal.SIG_IGN); time.sleep(30)",
            ],
            start_new_session=True,
        )
        with open(sys.argv[1], "w") as handle:
            handle.write(str(child.pid))
        time.sleep(30)
        """
        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        let process = try SpawnedProcessGroup.launch(
            binary: "/usr/bin/python3",
            arguments: ["-c", script, childPIDFile.path],
            environment: ProcessInfo.processInfo.environment,
            stdoutPipe: stdoutPipe,
            stderrPipe: stderrPipe)

        var childPID: pid_t?
        for _ in 0..<100 {
            if let text = try? String(contentsOf: childPIDFile, encoding: .utf8) {
                childPID = pid_t(text.trimmingCharacters(in: .whitespacesAndNewlines))
                break
            }
            try await Task.sleep(for: .milliseconds(20))
        }
        let escapedPID = try #require(childPID)
        defer { _ = kill(escapedPID, SIGKILL) }

        let start = Date()
        await process.terminate(grace: 0.3)
        let elapsed = Date().timeIntervalSince(start)

        #expect(elapsed >= 0.25, "Termination should honor the grace period before SIGKILL")
        #expect(kill(escapedPID, 0) == -1)
    }
}
