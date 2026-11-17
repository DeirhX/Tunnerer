// The tutorial card, the game-event toasts, the build-key hint and the warning banner. What they say is chosen in
// C# by the desktop's PlayerGuidance (each frame after a step carries it as `guidance`); this file only shows it.
// Tones are HintTone names: Dim, Info, Good, Warn, Bad, Accent (style.css: .tone-*).

const $ = (id) => document.getElementById(id);

const OBJECTIVE =
    "Link relays toward the hot core, then keep its power output above the goal to win.";

export class GuidanceView {
    // F2 or the card's button hides the tutorial; the objective shows in its place.
    tutorialHidden = false;
    guidance = null;
    goal = null;
    toasts = new Map();

    constructor() {
        $("tutorial-toggle").onclick = () => this.toggleTutorial();
    }

    toggleTutorial() {
        this.tutorialHidden = !this.tutorialHidden;
        this.showCard();
    }

    // The tutorial's target cell while its card shows, for the renderer's pointer.
    get target() {
        return this.tutorialHidden
            ? null
            : (this.guidance?.tutorial?.target ?? null);
    }

    update(guidance, goal) {
        this.guidance = guidance;
        this.goal = goal;
        this.showCard();
        this.showToasts(guidance.toasts);
        this.showHint(guidance.hint);
        this.showWarning(guidance.warning);
    }

    clear() {
        this.guidance = this.goal = null;
        this.toasts.clear();
        $("toasts").replaceChildren();
        $("warning").hidden = true;
    }

    showCard() {
        const tutorial = this.guidance?.tutorial;
        const showTutorial = tutorial && !this.tutorialHidden;
        $("tutorial-toggle").hidden = !tutorial;
        $("tutorial-toggle").firstChild.textContent = showTutorial
            ? "Hide "
            : "Tutorial ";
        $("mission-label").textContent = showTutorial
            ? `NEXT STEP ${tutorial.number} / ${tutorial.count}`
            : "SECTOR OBJECTIVE";
        $("mission-title").textContent = showTutorial
            ? tutorial.title
            : "Tap the hot core";
        $("guidance").textContent = showTutorial ? tutorial.body : OBJECTIVE;
        const progress = showTutorial && tutorial.progress;
        $("tutorial-progress").hidden = !progress;
        if (progress) {
            $("tutorial-meter").value = tutorial.fraction;
            $("tutorial-meter").classList.toggle(
                "done",
                tutorial.fraction >= 1,
            );
            $("tutorial-progress-text").textContent = tutorial.progress;
        }
        const g = this.goal;
        $("goal").textContent = g
            ? `${g.output.toFixed(0)} / ${g.required.toFixed(0)} POWER · ${g.hold.toFixed(0)} / ${g.requiredHold.toFixed(0)}s HELD`
            : "";
    }

    // Newest on top, as on the desktop. A toast keeps its element while it shows, so it animates in only once.
    showToasts(toasts) {
        const list = $("toasts"),
            live = new Map();
        for (const t of toasts) {
            const id = `${t.key}@${t.postedAt}`;
            let element = this.toasts.get(id);
            if (!element) {
                element = document.createElement("div");
                const title = document.createElement("strong");
                title.textContent = t.title;
                const detail = document.createElement("span");
                detail.textContent = t.detail;
                element.append(title, detail);
                element.className = `toast tone-${t.tone}`;
            }
            live.set(id, element);
        }
        this.toasts = live;
        list.replaceChildren(...[...live.values()].reverse());
    }

    showHint(hint) {
        const element = $("hint");
        element.textContent = hint.text;
        element.className = `tone-${hint.tone}`;
        element.hidden = !hint.text;
    }

    showWarning(warning) {
        const element = $("warning");
        element.hidden = !warning;
        if (!warning) return;
        element.className = `tone-${warning.tone}${warning.blinks ? " blink" : ""}`;
        element.dataset.kind = warning.kind;
        $("warning-title").textContent = warning.title;
        $("warning-detail").textContent = warning.detail ?? "";
        $("warning-detail").hidden = !warning.detail;
    }
}
