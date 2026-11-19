// The desktop builds the welcome screen offers (index.html, the Desktop section). The markup lists every platform, in
// the order written, and a platform with a published build has a link in its row; without JavaScript that list is
// the whole feature. This module works out which platform the visitor is on and puts that row first, saying so, or
// says that no build exists for it. It never adds a link: the URLs live in index.html only.

const NAMES = { windows: "Windows", macos: "macOS", linux: "Linux" };

// The platform a browser is on: 'windows', 'macos', 'linux', 'mobile' (a phone or tablet, which has no desktop build)
// or 'unknown'. It takes the navigator's fields, so tools/browser/downloads.test.mjs can run it on each platform's
// strings. The client hints (navigator.userAgentData) decide where the browser has them. Elsewhere the user agent and
// platform strings do. An iPad in desktop mode reports a Mac, and only its touch points give it away.
export function detectPlatform(nav) {
    const ua = nav.userAgent ?? "";
    const platform = nav.platform ?? "";
    const hints = nav.userAgentData;
    if (hints?.mobile || hints?.platform === "Android" || hints?.platform === "iOS") return "mobile";
    const named = { Windows: "windows", macOS: "macos", Linux: "linux" }[hints?.platform];
    if (named) return named;
    if (/Android|iPhone|iPod|iPad/.test(ua)) return "mobile";
    if (/^Mac/.test(platform) && (nav.maxTouchPoints ?? 0) > 1) return "mobile";
    // Chrome OS has no build; its user agent names X11 and Linux too, so it is checked first.
    if (/CrOS/.test(ua)) return "unknown";
    if (/Windows/.test(ua) || /^Win/.test(platform)) return "windows";
    // Apple silicon Macs report the same Intel string as Intel ones, so one macOS build is offered for both.
    if (/Macintosh|Mac OS X/.test(ua) || /^Mac/.test(platform)) return "macos";
    if (/Linux|X11/.test(ua) || /Linux/.test(platform)) return "linux";
    return "unknown";
}

// Marks the visitor's platform in the Desktop section: its row moves first and is styled as the main download, and
// the lead line says whether a build exists for it. A phone, or a platform the page does not know, keeps the list
// and the lead as written.
export function offerDownloads(doc, platform) {
    const list = doc.querySelector(".download-list");
    const lead = doc.getElementById("downloads-lead");
    if (!list || !lead) return;
    if (platform === "mobile") {
        lead.textContent = "Desktop builds are for computers. On a phone or tablet, play the browser edition on this page.";
        return;
    }
    const name = NAMES[platform];
    if (!name) return;
    const row = [...list.children].find((item) => item.dataset.os === platform);
    if (!row) return;
    if (row.querySelector("a.download-link")) {
        lead.textContent = `Your computer looks like ${name}, so its build comes first. The others are below.`;
        list.prepend(row);
        row.classList.add("is-suggested");
    } else {
        lead.textContent = `No ${name} build is published yet. The other platforms are below.`;
    }
}
