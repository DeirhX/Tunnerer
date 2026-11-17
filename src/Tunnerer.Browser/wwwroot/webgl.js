// Draws the desktop shaders, translated at publish time from their original HLSL.
export class WebGlRenderer {
    constructor(canvas) {
        this.canvas = canvas;
        this.gl = canvas.getContext("webgl2", {
            alpha: false,
            antialias: false,
            depth: false,
            stencil: false,
        });
        if (!this.gl)
            throw new Error(
                "WebGL 2 is unavailable. Enable hardware acceleration and reload the page.",
            );
        this.mapTextures = [];
        this.mapWidth = this.mapHeight = 0;
        this.targetWidth = this.targetHeight = 0;
        this.ready = false;
        this.samplerBindings = new Map();
        canvas.addEventListener("webglcontextlost", (e) => {
            e.preventDefault();
            this.error = new Error(
                "The graphics context was lost. Reload the page to restore the game.",
            );
        });
    }

    async initialize() {
        if (this.ready) return;
        const gl = this.gl;
        const names = [
            "FullscreenVs",
            "FullscreenTargetVs",
            "TerrainPs",
            "PostPs",
            "EntityVs",
            "EntityPs",
        ];
        const source = Object.fromEntries(
            await Promise.all(
                names.map(async (name) => {
                    const response = await fetch(
                        new URL(`./shaders/${name}.glsl`, import.meta.url),
                    );
                    if (!response.ok)
                        throw new Error(
                            `Could not load ${name} shader (${response.status}).`,
                        );
                    return [name, await response.text()];
                }),
            ),
        );
        this.terrainProgram = this.program(
            source.FullscreenTargetVs,
            source.TerrainPs,
        );
        this.postProgram = this.program(source.FullscreenVs, source.PostPs);
        this.entityProgram = this.program(source.EntityVs, source.EntityPs);
        this.uniforms = gl.createBuffer();
        gl.bindBufferBase(gl.UNIFORM_BUFFER, 0, this.uniforms);
        this.entityBuffer = gl.createBuffer();
        this.entityVao = gl.createVertexArray();
        this.emptyVao = gl.createVertexArray();
        gl.bindVertexArray(this.entityVao);
        gl.bindBuffer(gl.ARRAY_BUFFER, this.entityBuffer);
        let offset = 0;
        for (const [index, count] of [2, 2, 4, 4].entries()) {
            gl.enableVertexAttribArray(index);
            gl.vertexAttribPointer(index, count, gl.FLOAT, false, 48, offset);
            offset += count * 4;
        }
        gl.bindVertexArray(null);
        this.framebuffer = gl.createFramebuffer();
        this.sceneTexture = this.texture();
        gl.disable(gl.DEPTH_TEST);
        gl.disable(gl.CULL_FACE);
        this.ready = true;
    }

    program(vertex, fragment) {
        const gl = this.gl,
            program = gl.createProgram();
        const shaders = [
            [gl.VERTEX_SHADER, vertex],
            [gl.FRAGMENT_SHADER, fragment],
        ].map(([type, source]) => {
            const shader = gl.createShader(type);
            gl.shaderSource(shader, source);
            gl.compileShader(shader);
            if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
                const error = gl.getShaderInfoLog(shader);
                gl.deleteShader(shader);
                throw new Error(`Shader compilation failed: ${error}`);
            }
            gl.attachShader(program, shader);
            return shader;
        });
        gl.linkProgram(program);
        for (const shader of shaders) {
            gl.detachShader(program, shader);
            gl.deleteShader(shader);
        }
        if (!gl.getProgramParameter(program, gl.LINK_STATUS))
            throw new Error(
                `Shader linking failed: ${gl.getProgramInfoLog(program)}`,
            );
        const block = gl.getUniformBlockIndex(program, "PostParamsBlock");
        if (block !== gl.INVALID_INDEX)
            gl.uniformBlockBinding(program, block, 0);
        return program;
    }

    texture() {
        const gl = this.gl,
            texture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        return texture;
    }

    resetWorld() {
        for (const texture of this.mapTextures) this.gl.deleteTexture(texture);
        this.mapTextures = [];
        this.mapWidth = this.mapHeight = 0;
        this.hasFrame = false;
    }

    accept(packet) {
        const gl = this.gl;
        const uniforms = new Float32Array(
            packet.uniforms.buffer,
            packet.uniforms.byteOffset,
            packet.uniforms.byteLength / 4,
        );
        const width = uniforms[4],
            height = uniforms[5];
        if (this.mapWidth !== width || this.mapHeight !== height) {
            this.resetWorld();
            this.mapWidth = width;
            this.mapHeight = height;
            for (let i = 0; i < 4; i++) {
                const texture = this.texture();
                gl.texImage2D(
                    gl.TEXTURE_2D,
                    0,
                    gl.RGBA8,
                    width,
                    height,
                    0,
                    gl.RGBA,
                    gl.UNSIGNED_BYTE,
                    null,
                );
                this.mapTextures.push(texture);
            }
        }
        const [x, y, w, h] = packet.rect;
        if (w && h) {
            for (let i = 0; i < 4; i++) {
                gl.bindTexture(gl.TEXTURE_2D, this.mapTextures[i]);
                gl.texSubImage2D(
                    gl.TEXTURE_2D,
                    0,
                    x,
                    y,
                    w,
                    h,
                    gl.RGBA,
                    gl.UNSIGNED_BYTE,
                    packet.textures[i],
                );
            }
        }
        this.width = uniforms[8];
        this.height = uniforms[9];
        this.scale = uniforms[2];
        this.cameraX = uniforms[6] / this.scale;
        this.cameraY = uniforms[7] / this.scale;
        gl.bindBuffer(gl.UNIFORM_BUFFER, this.uniforms);
        gl.bufferData(gl.UNIFORM_BUFFER, packet.uniforms, gl.DYNAMIC_DRAW);
        gl.bindBuffer(gl.ARRAY_BUFFER, this.entityBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, packet.vertices, gl.DYNAMIC_DRAW);
        this.vertexCount = packet.vertices.byteLength / 48;
        this.hasFrame = true;
    }

    bindTextures(program, textures, sceneName) {
        const gl = this.gl;
        gl.useProgram(program);
        if (!this.samplerBindings.has(program)) {
            const count = gl.getProgramParameter(program, gl.ACTIVE_UNIFORMS);
            for (let i = 0; i < count; i++) {
                const uniform = gl.getActiveUniform(program, i);
                if (uniform.type !== gl.SAMPLER_2D) continue;
                const unit = uniform.name.startsWith(sceneName)
                    ? 0
                    : uniform.name.startsWith("auxTex")
                      ? 1
                      : uniform.name.startsWith("coverageTex")
                        ? 2
                        : 3;
                gl.uniform1i(
                    gl.getUniformLocation(program, uniform.name),
                    unit,
                );
            }
            this.samplerBindings.set(program, true);
        }
        for (let unit = 0; unit < textures.length; unit++) {
            gl.activeTexture(gl.TEXTURE0 + unit);
            gl.bindTexture(gl.TEXTURE_2D, textures[unit]);
        }
    }

    draw() {
        if (this.error) throw this.error;
        if (!this.hasFrame) return;
        const gl = this.gl;
        if (
            this.targetWidth !== this.width ||
            this.targetHeight !== this.height
        ) {
            this.targetWidth = this.width;
            this.targetHeight = this.height;
            this.canvas.width = this.width;
            this.canvas.height = this.height;
            gl.bindTexture(gl.TEXTURE_2D, this.sceneTexture);
            gl.texImage2D(
                gl.TEXTURE_2D,
                0,
                gl.RGBA8,
                this.width,
                this.height,
                0,
                gl.RGBA,
                gl.UNSIGNED_BYTE,
                null,
            );
            gl.bindFramebuffer(gl.FRAMEBUFFER, this.framebuffer);
            gl.framebufferTexture2D(
                gl.FRAMEBUFFER,
                gl.COLOR_ATTACHMENT0,
                gl.TEXTURE_2D,
                this.sceneTexture,
                0,
            );
            if (
                gl.checkFramebufferStatus(gl.FRAMEBUFFER) !==
                gl.FRAMEBUFFER_COMPLETE
            )
                throw new Error("Could not create the terrain render target.");
        }
        gl.viewport(0, 0, this.width, this.height);
        gl.disable(gl.BLEND);
        gl.bindVertexArray(this.emptyVao);
        gl.bindFramebuffer(gl.FRAMEBUFFER, this.framebuffer);
        this.bindTextures(this.terrainProgram, this.mapTextures, "sourceTex");
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        this.bindTextures(
            this.postProgram,
            [this.sceneTexture, ...this.mapTextures.slice(1)],
            "sceneTex",
        );
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.useProgram(this.entityProgram);
        gl.bindVertexArray(this.entityVao);
        gl.drawArrays(gl.TRIANGLES, 0, this.vertexCount);
        gl.disable(gl.BLEND);
        gl.bindVertexArray(null);
    }
}
