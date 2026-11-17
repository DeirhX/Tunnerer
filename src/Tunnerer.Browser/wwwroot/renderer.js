import { WebGlRenderer } from "./webgl.js";

export class GameRenderer {
    constructor(canvas) {
        this.canvas = canvas;
        this.ctx = canvas.getContext("2d");
        this.worldCanvas = document.createElement("canvas");
        this.worldCanvas.id = "world";
        this.worldCanvas.style.pointerEvents = "none";
        this.worldCanvas.setAttribute("aria-hidden", "true");
        canvas.before(this.worldCanvas);
        this.zoom = 6;
        this.frame = null;
        this.resize();
        window.addEventListener("resize", () => this.resize());
    }

    async initialize() {
        this.gpu ||= new WebGlRenderer(this.worldCanvas);
        await this.gpu.initialize();
        this.gpu.resetWorld();
    }

    resize() {
        const r = this.canvas.getBoundingClientRect();
        this.width = r.width;
        this.height = r.height;
        this.dpr = Math.min(
            devicePixelRatio || 1,
            2,
            3840 / r.width,
            2160 / r.height,
        );
        this.canvas.width = Math.round(r.width * this.dpr);
        this.canvas.height = Math.round(r.height * this.dpr);
    }

    viewSize() {
        const width = this.canvas.width,
            height = this.canvas.height;
        const scale = Math.max(
            1,
            Math.round(this.zoom * this.dpr),
            Math.ceil(width / 620),
            Math.ceil(height / 380),
        );
        return { width, height, scale };
    }

    accept(state, packet) {
        this.frame = state;
        this.gpu.accept(packet);
    }

    draw(now) {
        const c = this.ctx;
        c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
        if (!this.frame) {
            this.worldCanvas.hidden = true;
            this.drawLanding(c, now);
            return;
        }
        this.worldCanvas.hidden = false;
        this.gpu.draw();
        c.clearRect(0, 0, this.width, this.height);
        if (this.frame.goal)
            this.bearing(c, this.frame.goal.position, "CORE", "#e9a367");
        if (!this.frame.player.docked)
            this.bearing(c, this.frame.player.home, "HOME", "#83d9cf");
    }

    bearing(c, target, label, color) {
        const scale = this.gpu.scale / this.dpr;
        const dx = (target.x + 0.5 - this.gpu.cameraX) * scale - this.width / 2;
        const dy =
            (target.y + 0.5 - this.gpu.cameraY) * scale - this.height / 2;
        const w = this.width / 2 - 28,
            h = this.height / 2 - 30;
        if (Math.abs(dx) < w && Math.abs(dy) < h) return;
        const ratio = Math.min(
            w / Math.max(Math.abs(dx), 1),
            h / Math.max(Math.abs(dy), 1),
        );
        const x = this.width / 2 + dx * ratio,
            y = this.height / 2 + dy * ratio;
        c.save();
        c.translate(x, y);
        c.rotate(Math.atan2(dy, dx));
        c.fillStyle = color;
        c.beginPath();
        c.moveTo(7, 0);
        c.lineTo(-4, -4);
        c.lineTo(-4, 4);
        c.closePath();
        c.fill();
        c.restore();
        c.fillStyle = color;
        c.font = "9px monospace";
        c.textAlign = dx < 0 ? "left" : "right";
        c.fillText(
            `${label} ${Math.round(Math.hypot(dx, dy) / scale)}`,
            x + (dx < 0 ? 10 : -10),
            y - 10,
        );
    }

    drawLanding(c, now) {
        c.fillStyle = "#0b1419";
        c.fillRect(0, 0, this.width, this.height);
        const cx = this.width * 0.79,
            cy = this.height * 0.48;
        const glow = c.createRadialGradient(
            cx,
            cy,
            5,
            cx,
            cy,
            this.height * 0.7,
        );
        glow.addColorStop(0, "#72422355");
        glow.addColorStop(1, "#0b141900");
        c.fillStyle = glow;
        c.fillRect(0, 0, this.width, this.height);
        c.strokeStyle = "#65847815";
        c.lineWidth = 1;
        for (let r = 40; r < this.height * 0.8; r += 22) {
            c.beginPath();
            for (let i = 0; i <= 100; i++) {
                const a = (i / 100) * Math.PI * 2;
                const radius =
                    r + Math.sin(a * 5 + r / 100) * 14 + Math.cos(a * 9) * 7;
                const x = cx + Math.cos(a) * radius * 1.05,
                    y = cy + Math.sin(a) * radius * 0.8;
                if (i === 0) c.moveTo(x, y);
                else c.lineTo(x, y);
            }
            c.closePath();
            c.stroke();
        }
        c.fillStyle = "#eeb065";
        c.beginPath();
        c.arc(cx, cy, 3, 0, Math.PI * 2);
        c.fill();
        c.strokeStyle = "#eeb06555";
        c.beginPath();
        c.arc(cx, cy, 13 + Math.sin(now / 1000) * 3, 0, Math.PI * 2);
        c.stroke();
        c.font = "10px monospace";
        c.fillStyle = "#ad916c";
        c.fillText("CORE / UNKNOWN DEPTH", cx + 25, cy + 4);
        const fade = c.createLinearGradient(0, 0, this.width, 0);
        fade.addColorStop(0, "#0b1419");
        fade.addColorStop(0.5, "#0b1419bb");
        fade.addColorStop(1, "#0b141900");
        c.fillStyle = fade;
        c.fillRect(0, 0, this.width, this.height);
    }
}
