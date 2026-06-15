#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif
import Foundation

package final class SpawnedProcessGroup: @unchecked Sendable {
    package enum LaunchError: LocalizedError {
        case setupFailed(String)
        case spawnFailed(String)

        package var errorDescription: String? {
            switch self {
            case let .setupFailed(details):
                "Failed to prepare process: \(details)"
            case let .spawnFailed(details):
                "Failed to launch process: \(details)"
            }
        }
    }

    private final class TerminationState: @unchecked Sendable {
        private let lock = NSLock()
        private var status: Int32?

        var value: Int32? {
            self.lock.withLock { self.status }
        }

        func resolve(_ status: Int32) {
            self.lock.withLock {
                guard self.status == nil else { return }
                self.status = status
            }
        }
    }

    package let pid: pid_t
    package let processGroup: pid_t
    private let termination = TerminationState()

    private init(pid: pid_t) {
        self.pid = pid
        self.processGroup = pid
        self.startWaiter()
    }

    package static func launch(
        binary: String,
        arguments: [String],
        environment: [String: String],
        stdoutPipe: Pipe,
        stderrPipe: Pipe) throws -> SpawnedProcessGroup
    {
        #if canImport(Darwin)
        var fileActions: posix_spawn_file_actions_t?
        #else
        var fileActions = posix_spawn_file_actions_t()
        #endif
        guard posix_spawn_file_actions_init(&fileActions) == 0 else {
            throw LaunchError.setupFailed("posix_spawn_file_actions_init")
        }
        defer { posix_spawn_file_actions_destroy(&fileActions) }

        let stdoutRead = stdoutPipe.fileHandleForReading.fileDescriptor
        let stdoutWrite = stdoutPipe.fileHandleForWriting.fileDescriptor
        let stderrRead = stderrPipe.fileHandleForReading.fileDescriptor
        let stderrWrite = stderrPipe.fileHandleForWriting.fileDescriptor
        var fileActionResults = [
            posix_spawn_file_actions_addopen(&fileActions, STDIN_FILENO, "/dev/null", O_RDONLY, 0),
            posix_spawn_file_actions_adddup2(&fileActions, stdoutWrite, STDOUT_FILENO),
            posix_spawn_file_actions_adddup2(&fileActions, stderrWrite, STDERR_FILENO),
        ]
        for descriptor in Self.pipeDescriptorsToClose([stdoutRead, stdoutWrite, stderrRead, stderrWrite]) {
            fileActionResults.append(posix_spawn_file_actions_addclose(&fileActions, descriptor))
        }
        guard fileActionResults.allSatisfy({ $0 == 0 }) else {
            throw LaunchError.setupFailed("posix_spawn file actions")
        }

        #if canImport(Darwin)
        var attributes: posix_spawnattr_t?
        #else
        var attributes = posix_spawnattr_t()
        #endif
        guard posix_spawnattr_init(&attributes) == 0 else {
            throw LaunchError.setupFailed("posix_spawnattr_init")
        }
        defer { posix_spawnattr_destroy(&attributes) }

        #if canImport(Darwin)
        let flags = POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_CLOEXEC_DEFAULT
        #else
        let flags = POSIX_SPAWN_SETPGROUP
        #endif
        guard posix_spawnattr_setflags(&attributes, Int16(flags)) == 0,
              posix_spawnattr_setpgroup(&attributes, 0) == 0
        else {
            throw LaunchError.setupFailed("posix_spawn process group")
        }

        var cArguments: [UnsafeMutablePointer<CChar>?] = ([binary] + arguments).map { strdup($0) }
        cArguments.append(nil)
        defer {
            for argument in cArguments {
                free(argument)
            }
        }

        var cEnvironment: [UnsafeMutablePointer<CChar>?] = environment.map { key, value in
            strdup("\(key)=\(value)")
        }
        cEnvironment.append(nil)
        defer {
            for entry in cEnvironment {
                free(entry)
            }
        }

        var pid: pid_t = 0
        let spawnResult = binary.withCString { path in
            posix_spawn(&pid, path, &fileActions, &attributes, cArguments, cEnvironment)
        }
        stdoutPipe.fileHandleForWriting.closeFile()
        stderrPipe.fileHandleForWriting.closeFile()
        guard spawnResult == 0 else {
            throw LaunchError.spawnFailed(String(cString: strerror(spawnResult)))
        }
        return SpawnedProcessGroup(pid: pid)
    }

    package var isRunning: Bool {
        self.termination.value == nil
    }

    package var terminationStatus: Int32? {
        self.termination.value
    }

    @discardableResult
    package func terminate(grace: TimeInterval = 0.4) async -> Int32? {
        if self.isRunning {
            let killDeadline = Date().addingTimeInterval(max(0, grace))
            let descendants = TTYProcessTreeTerminator.descendantPIDs(of: self.pid)
            let descendantIdentities = descendants.compactMap(TTYProcessTreeTerminator.processIdentity(for:))
            TTYProcessTreeTerminator.terminateProcessTree(
                rootPID: self.pid,
                processGroup: self.processGroup,
                signal: SIGTERM,
                knownDescendants: descendants)
            _ = await self.waitForExit(timeout: max(0, killDeadline.timeIntervalSinceNow))
            while descendantIdentities.contains(where: TTYProcessTreeTerminator.isCurrent(_:)),
                  Date() < killDeadline
            {
                try? await Task.sleep(for: .milliseconds(20))
            }

            let currentDescendants = descendantIdentities
                .filter(TTYProcessTreeTerminator.isCurrent(_:))
                .map(\.pid)
            if self.isRunning {
                TTYProcessTreeTerminator.terminateProcessTree(
                    rootPID: self.pid,
                    processGroup: self.processGroup,
                    signal: SIGKILL,
                    knownDescendants: currentDescendants)
                _ = await self.waitForExit(timeout: grace)
            } else {
                for pid in currentDescendants {
                    kill(pid, SIGKILL)
                }
            }
            return self.terminationStatus
        }
        await self.terminateResidualGroup(grace: grace)
        return self.terminationStatus
    }

    package func terminateResidualGroup(grace: TimeInterval = 0.4) async {
        guard Self.processGroupExists(self.processGroup) else { return }
        Self.signal(processGroup: self.processGroup, signal: SIGTERM)
        guard await !self.waitForGroupExit(timeout: grace) else { return }
        Self.signal(processGroup: self.processGroup, signal: SIGKILL)
        _ = await self.waitForGroupExit(timeout: grace)
    }

    private func startWaiter() {
        let pid = self.pid
        let termination = self.termination
        DispatchQueue.global(qos: .userInitiated).async {
            var rawStatus: Int32 = 0
            var result: pid_t
            repeat {
                result = waitpid(pid, &rawStatus, 0)
            } while result == -1 && errno == EINTR

            let status = result == pid ? Self.exitStatus(from: rawStatus) : 1
            termination.resolve(status)
        }
    }

    private func waitForExit(timeout: TimeInterval) async -> Int32? {
        let deadline = Date().addingTimeInterval(max(0, timeout))
        while self.terminationStatus == nil, Date() < deadline {
            try? await Task.sleep(for: .milliseconds(20))
        }
        return self.terminationStatus
    }

    private func waitForGroupExit(timeout: TimeInterval) async -> Bool {
        let deadline = Date().addingTimeInterval(max(0, timeout))
        while Self.processGroupExists(self.processGroup), Date() < deadline {
            try? await Task.sleep(for: .milliseconds(20))
        }
        return !Self.processGroupExists(self.processGroup)
    }

    private static func processGroupExists(_ processGroup: pid_t) -> Bool {
        errno = 0
        return kill(-processGroup, 0) == 0 || errno == EPERM
    }

    private static func signal(processGroup: pid_t, signal: Int32) {
        _ = kill(-processGroup, signal)
    }

    package static func pipeDescriptorsToClose(_ descriptors: [Int32]) -> [Int32] {
        Array(Set(descriptors.filter { $0 > STDERR_FILENO })).sorted()
    }

    private static func exitStatus(from rawStatus: Int32) -> Int32 {
        let signal = rawStatus & 0x7F
        return signal == 0 ? (rawStatus >> 8) & 0xFF : signal
    }
}
