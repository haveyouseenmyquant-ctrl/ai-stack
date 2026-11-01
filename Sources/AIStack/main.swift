import AppKit

// Writing to a Claude CLI pipe after the process exits must raise an error, not kill the app.
signal(SIGPIPE, SIG_IGN)
SelfTest.runIfRequested()
DoctorCLI.runIfRequested()

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate() // NSApplication.delegate is weak; run() keeps this scope alive.
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
