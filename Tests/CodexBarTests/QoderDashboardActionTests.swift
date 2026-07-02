import AppKit
import CodexBarCore
import Testing
@testable import CodexBar

@MainActor
@Suite(.serialized)
struct QoderDashboardActionTests {
    private func makeSettings() -> SettingsStore {
        let suite = "QoderDashboardActionTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        let settings = SettingsStore(
            userDefaults: defaults,
            configStore: testConfigStore(suiteName: suite),
            zaiTokenStore: NoopZaiTokenStore(),
            syntheticTokenStore: NoopSyntheticTokenStore())
        settings.providerDetectionCompleted = true
        settings.statusChecksEnabled = false
        settings.refreshFrequency = .manual
        return settings
    }

    private func makeController(settings: SettingsStore) -> (StatusItemController, UsageStore) {
        StatusItemController.menuCardRenderingEnabled = false
        StatusItemController.setMenuRefreshEnabledForTesting(false)
        let fetcher = UsageFetcher()
        let store = UsageStore(fetcher: fetcher, browserDetection: BrowserDetection(cacheTTL: 0), settings: settings)
        let controller = StatusItemController(
            store: store,
            settings: settings,
            account: fetcher.loadAccountInfo(),
            updater: DisabledUpdaterController(),
            preferencesSelection: PreferencesSelection(),
            statusBar: .system)
        return (controller, store)
    }

    @Test
    func `qoder dashboard action follows current manual header`() {
        let settings = self.makeSettings()
        settings.qoderCookieSource = .manual
        settings.qoderCookieHeader = "curl https://qoder.com.cn -H 'Cookie: sid=abc'"
        let (controller, store) = self.makeController(settings: settings)
        store.lastSourceLabels[.qoder] = "manual / qoder.com"

        #expect(controller.dashboardURL(for: .qoder) == QoderWebSite.china.dashboardURL)

        settings.qoderCookieHeader = "curl https://qoder.com -H 'Host: qoder.com.cn' -H 'Cookie: sid=abc'"
        store.lastSourceLabels[.qoder] = "manual / qoder.com.cn"
        #expect(controller.dashboardURL(for: .qoder) == QoderWebSite.international.dashboardURL)
    }

    @Test
    func `qoder dashboard action follows store source label in auto mode`() {
        let settings = self.makeSettings()
        settings.qoderCookieSource = .auto
        let (controller, store) = self.makeController(settings: settings)

        store.lastSourceLabels[.qoder] = "Chrome / qoder.com.cn"
        #expect(controller.dashboardURL(for: .qoder) == QoderWebSite.china.dashboardURL)

        store.lastSourceLabels[.qoder] = "Chrome / qoder.com"
        #expect(controller.dashboardURL(for: .qoder) == QoderWebSite.international.dashboardURL)
    }
}
