import AppKit

extension StatusItemController {
    struct MenuCardHeightCacheKey: Hashable {
        let id: String
        let scope: String
        let width: Int
        let fingerprint: String
    }

    func cachedMenuCardHeight(
        for id: String,
        scope: String,
        width: CGFloat,
        fingerprint: String? = nil,
        measure: () -> CGFloat) -> CGFloat
    {
        let key = MenuCardHeightCacheKey(
            id: id,
            scope: scope,
            width: Int((width * 100).rounded()),
            fingerprint: fingerprint ?? "version:\(self.menuContentVersion)")
        if let cached = self.menuCardHeightCache[key] {
            return cached
        }
        let height = measure()
        if self.menuCardHeightCache.count > 256 {
            self.menuCardHeightCache.removeAll(keepingCapacity: true)
        }
        self.menuCardHeightCache[key] = height
        return height
    }

    func pruneVersionScopedMenuCardHeightCache() {
        let currentVersionFingerprint = "version:\(self.menuContentVersion)"
        for key in self.menuCardHeightCache.keys
            where key.fingerprint.hasPrefix("version:") && key.fingerprint != currentVersionFingerprint
        {
            self.menuCardHeightCache.removeValue(forKey: key)
        }
    }
}
