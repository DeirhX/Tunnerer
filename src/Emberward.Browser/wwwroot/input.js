export class GameInput {
    constructor(canvas, action) {
        this.keys = new Set();
        this.actions = [];
        this.pointer = null;
        this.firing = false;
        this.touchMoves = new Map();
        this.releaseAction = false;
        this.enabled = false;
        const movement = [
            "KeyW",
            "KeyA",
            "KeyS",
            "KeyD",
            "ArrowUp",
            "ArrowDown",
            "ArrowLeft",
            "ArrowRight",
            "Space",
        ];
        window.addEventListener("keydown", (e) => {
            if (e.target instanceof HTMLInputElement) return;
            if (this.enabled && movement.includes(e.code)) e.preventDefault();
            if (!e.repeat) {
                if (e.code === "Escape") {
                    e.preventDefault();
                    action("pause");
                }
                if (e.key === "?") action("help");
                // A network match: the host starts it from the lobby, and P holds it for everyone.
                if (e.code === "Enter" || e.code === "NumpadEnter")
                    action("enter");
                if (e.code === "KeyP") action("netpause");
                if (e.code === "F2") {
                    e.preventDefault();
                    action("tutorial");
                }
                if (this.enabled) {
                    if (e.code === "KeyH") action("thermal");
                    if (e.code === "KeyG") action("power");
                    // The upgrade panel of the machine next to the tank; while it is open, 1-3 buy for that machine.
                    if (e.code === "KeyF") this.queue("machineUpgrade", true);
                    const build = { KeyE: 1, KeyR: 2, KeyY: 3, KeyU: 4 }[e.code];
                    if (build) this.queue("build", build);
                    if (e.code === "KeyX") this.queue("recall", true);
                    if (/^Digit[1-7]$/.test(e.code))
                        this.queueUpgrade(Number(e.code.slice(-1)), this.machinePanelOpen);
                    if (["Equal", "NumpadAdd"].includes(e.code))
                        action("zoom", 1);
                    if (["Minus", "NumpadSubtract"].includes(e.code))
                        action("zoom", -1);
                }
            }
            if (this.enabled) this.keys.add(e.code);
        });
        window.addEventListener("keyup", (e) => this.keys.delete(e.code));
        canvas.addEventListener("pointermove", (e) => {
            if (!this.enabled) return;
            const r = canvas.getBoundingClientRect();
            this.pointer = {
                x: e.clientX - r.left - r.width / 2,
                y: e.clientY - r.top - r.height / 2,
                mouse: e.pointerType === "mouse",
            };
        });
        canvas.addEventListener("pointerdown", (e) => {
            if (!this.enabled || e.button !== 0) return;
            canvas.focus({ preventScroll: true });
            const r = canvas.getBoundingClientRect();
            const pointer = {
                x: e.clientX - r.left - r.width / 2,
                y: e.clientY - r.top - r.height / 2,
                mouse: e.pointerType === "mouse",
            };
            // On touch, a tap on the lit machine opens (or closes) its upgrade panel instead of firing. A mouse
            // click always fires: F opens the panel there.
            if (!pointer.mouse && this.tapMachine?.(pointer)) {
                this.queue("machineUpgrade", true);
                return;
            }
            canvas.setPointerCapture(e.pointerId);
            this.firing = true;
            this.pointer = pointer;
        });
        const release = () => {
            this.firing = false;
        };
        canvas.addEventListener("pointerup", release);
        canvas.addEventListener("pointercancel", release);
        canvas.addEventListener("lostpointercapture", release);
        canvas.addEventListener("contextmenu", (e) => e.preventDefault());
        canvas.addEventListener(
            "wheel",
            (e) => {
                if (!this.enabled) return;
                e.preventDefault();
                action("zoom", e.deltaY < 0 ? 1 : -1);
            },
            { passive: false },
        );
        document.querySelectorAll("[data-move]").forEach((button) => {
            button.addEventListener("pointerdown", (e) => {
                if (!this.enabled) return;
                e.preventDefault();
                button.setPointerCapture(e.pointerId);
                this.pointer = null;
                this.touchMoves.set(
                    e.pointerId,
                    button.dataset.move.split(",").map(Number),
                );
            });
            for (const type of [
                "pointerup",
                "pointercancel",
                "lostpointercapture",
            ])
                button.addEventListener(type, (e) =>
                    this.touchMoves.delete(e.pointerId),
                );
        });
        const fire = document.getElementById("fire");
        fire.addEventListener("pointerdown", (e) => {
            if (!this.enabled) return;
            e.preventDefault();
            fire.setPointerCapture(e.pointerId);
            this.firing = true;
        });
        for (const type of ["pointerup", "pointercancel", "lostpointercapture"])
            fire.addEventListener(type, release);
    }

    queue(type, value) {
        if (this.enabled && this.actions.length < 16)
            this.actions.push({ [type]: value });
    }

    // An upgrade key, marked for the machine when it was pressed with its panel open (or is one of the panel's
    // choices): it stays a machine purchase even if an earlier one closes the panel before it is sent.
    queueUpgrade(key, forMachine) {
        if (this.enabled && this.actions.length < 16)
            this.actions.push({ upgrade: key, upgradeMachine: !!forMachine });
    }

    clear() {
        this.keys.clear();
        this.touchMoves.clear();
        this.actions = [];
        this.firing = false;
        this.pointer = null;
        // Send a release tick before a new action when resuming after a build press.
    }

    read() {
        const held = (...codes) =>
            codes.some((c) => this.keys.has(c)) ? 1 : 0;
        let x = held("KeyD", "ArrowRight") - held("KeyA", "ArrowLeft");
        let y = held("KeyS", "ArrowDown") - held("KeyW", "ArrowUp");
        for (const [tx, ty] of this.touchMoves.values()) {
            x += tx;
            y += ty;
        }
        const action = this.releaseAction ? {} : this.actions.shift() || {};
        this.releaseAction = !!(action.build || action.upgrade || action.recall);
        return {
            x: Math.sign(x),
            y: Math.sign(y),
            fire: this.firing || !!held("Space"),
            aimX: this.pointer ? this.pointer.x - (this.aimOffset || 0) : 0,
            aimY: this.pointer ? this.pointer.y - (this.aimOffset || 0) : 0,
            build: action.build || 0,
            recall: !!action.recall,
            upgrade: action.upgrade || 0,
            machineUpgrade: !!action.machineUpgrade,
            upgradeMachine: !!action.upgradeMachine,
        };
    }
}
