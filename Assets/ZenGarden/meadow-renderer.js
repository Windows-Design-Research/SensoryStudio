(function () {
  'use strict';

  const MOSS_ISLANDS = [];

  const ORNAMENTAL_CLUMPS = [
    {
      x: 0.340,
      z: 0.455,
      radius: 0.036,
      length: 0.064,
      height: 0.060,
      rotation: -2.08,
      color: [0.40, 0.49, 0.27],
      leaves: 18
    },
    {
      x: 0.845,
      z: 0.470,
      radius: 0.031,
      length: 0.054,
      height: 0.052,
      rotation: -2.72,
      color: [0.43, 0.51, 0.28],
      leaves: 16
    }
  ];

  function clamp(value, minimum, maximum) {
    return Math.max(minimum, Math.min(maximum, value));
  }

  function smoothstep(edge0, edge1, value) {
    const amount = clamp((value - edge0) / (edge1 - edge0), 0, 1);
    return amount * amount * (3 - 2 * amount);
  }

  function seededRandom(seed) {
    let state = seed >>> 0;
    return function () {
      state += 0x6D2B79F5;
      let value = state;
      value = Math.imul(value ^ value >>> 15, value | 1);
      value ^= value + Math.imul(value ^ value >>> 7, value | 61);
      return ((value ^ value >>> 14) >>> 0) / 4294967296;
    };
  }

  function hashNoise(x, z) {
    const value = Math.sin(x * 127.1 + z * 311.7) * 43758.5453;
    return value - Math.floor(value);
  }

  function valueNoise(x, z) {
    const x0 = Math.floor(x);
    const z0 = Math.floor(z);
    const tx = smoothstep(0, 1, x - x0);
    const tz = smoothstep(0, 1, z - z0);
    const a = hashNoise(x0, z0);
    const b = hashNoise(x0 + 1, z0);
    const c = hashNoise(x0, z0 + 1);
    const d = hashNoise(x0 + 1, z0 + 1);
    return (a + (b - a) * tx) +
      ((c + (d - c) * tx) - (a + (b - a) * tx)) * tz;
  }

  function lobeDensity(nx, nz, lobe) {
    const dx = (nx - lobe[0]) / lobe[2];
    const dz = (nz - lobe[1]) / lobe[3];
    const edgeBlur = clamp(lobe[5] || 0, 0, 1);
    const feather = 0.27 + edgeBlur * 0.25;
    return 1 - smoothstep(
      0.75 - feather,
      0.75 + feather,
      Math.hypot(dx, dz));
  }

  function islandSample(island, nx, nz) {
    let remaining = 1;
    let height = 0;
    let edgeBlur = 0;
    for (const lobe of island.lobes) {
      const density = lobeDensity(nx, nz, lobe);
      const lobeEdgeBlur = clamp(lobe[5] || 0, 0, 1);
      remaining *= 1 - density;
      height = Math.max(height, density * lobe[4]);
      edgeBlur = Math.max(
        edgeBlur,
        smoothstep(0.02, 0.18, density) * lobeEdgeBlur);
    }
    const rawDensity = 1 - remaining;
    const edgeNoise = (valueNoise(nx * 17.0 + 4.3, nz * 17.0 + 8.7) - 0.5) *
      0.12 * (1 - edgeBlur * 0.78);
    const density = clamp(rawDensity + edgeNoise * (1 - rawDensity), 0, 1);
    const roughMoundNoise =
      0.88 + valueNoise(nx * 8.2 + 2.1, nz * 8.2 + 5.4) * 0.16;
    return {
      density,
      height: height * roughMoundNoise * smoothstep(0.02, 0.72, density),
      edgeBlur
    };
  }

  function islandBounds(island) {
    let left = 1;
    let right = 0;
    let top = 1;
    let bottom = 0;
    for (const lobe of island.lobes) {
      left = Math.min(left, lobe[0] - lobe[2] * 1.08);
      right = Math.max(right, lobe[0] + lobe[2] * 1.08);
      top = Math.min(top, lobe[1] - lobe[3] * 1.08);
      bottom = Math.max(bottom, lobe[1] + lobe[3] * 1.08);
    }
    return { left, right, top, bottom };
  }

  function invert4(matrix) {
    const m = matrix;
    const result = new Float32Array(16);
    const b00 = m[0] * m[5] - m[1] * m[4];
    const b01 = m[0] * m[6] - m[2] * m[4];
    const b02 = m[0] * m[7] - m[3] * m[4];
    const b03 = m[1] * m[6] - m[2] * m[5];
    const b04 = m[1] * m[7] - m[3] * m[5];
    const b05 = m[2] * m[7] - m[3] * m[6];
    const b06 = m[8] * m[13] - m[9] * m[12];
    const b07 = m[8] * m[14] - m[10] * m[12];
    const b08 = m[8] * m[15] - m[11] * m[12];
    const b09 = m[9] * m[14] - m[10] * m[13];
    const b10 = m[9] * m[15] - m[11] * m[13];
    const b11 = m[10] * m[15] - m[11] * m[14];
    let determinant =
      b00 * b11 - b01 * b10 + b02 * b09 +
      b03 * b08 - b04 * b07 + b05 * b06;
    if (!determinant) return null;
    determinant = 1 / determinant;
    result[0] = (m[5] * b11 - m[6] * b10 + m[7] * b09) * determinant;
    result[1] = (-m[1] * b11 + m[2] * b10 - m[3] * b09) * determinant;
    result[2] = (m[13] * b05 - m[14] * b04 + m[15] * b03) * determinant;
    result[3] = (-m[9] * b05 + m[10] * b04 - m[11] * b03) * determinant;
    result[4] = (-m[4] * b11 + m[6] * b08 - m[7] * b07) * determinant;
    result[5] = (m[0] * b11 - m[2] * b08 + m[3] * b07) * determinant;
    result[6] = (-m[12] * b05 + m[14] * b02 - m[15] * b01) * determinant;
    result[7] = (m[8] * b05 - m[10] * b02 + m[11] * b01) * determinant;
    result[8] = (m[4] * b10 - m[5] * b08 + m[7] * b06) * determinant;
    result[9] = (-m[0] * b10 + m[1] * b08 - m[3] * b06) * determinant;
    result[10] = (m[12] * b04 - m[13] * b02 + m[15] * b00) * determinant;
    result[11] = (-m[8] * b04 + m[9] * b02 - m[11] * b00) * determinant;
    result[12] = (-m[4] * b09 + m[5] * b07 - m[6] * b06) * determinant;
    result[13] = (m[0] * b09 - m[1] * b07 + m[2] * b06) * determinant;
    result[14] = (-m[12] * b03 + m[13] * b01 - m[14] * b00) * determinant;
    result[15] = (m[8] * b03 - m[9] * b01 + m[10] * b00) * determinant;
    return result;
  }

  function transform4(matrix, vector) {
    const x = vector[0];
    const y = vector[1];
    const z = vector[2];
    const w = vector[3];
    return [
      matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12] * w,
      matrix[1] * x + matrix[5] * y + matrix[9] * z + matrix[13] * w,
      matrix[2] * x + matrix[6] * y + matrix[10] * z + matrix[14] * w,
      matrix[3] * x + matrix[7] * y + matrix[11] * z + matrix[15] * w
    ];
  }

  class GardenVegetationRenderer {
    constructor(gl, options = {}) {
      this.gl = gl;
      this.originalVao = options.originalVao || null;
      this.reducedMotion = window.matchMedia(
        '(prefers-reduced-motion: reduce)').matches;
      this.aspect = 0;
      this.pointer = new Float32Array([-100, -100]);
      this.pointerTarget = new Float32Array([-100, -100]);
      this.pointerVelocity = new Float32Array([0, 0]);
      this.pointerStrength = 0;
      this.lastPointerTime = -Infinity;
      this.lastFrameTime = performance.now();
      this.mossIslands = MOSS_ISLANDS.map(island => ({
        ...island,
        lobes: island.lobes.map(lobe => [...lobe])
      }));
      this.ornamentalClumps = ORNAMENTAL_CLUMPS.map(clump => ({ ...clump }));
      this.leafyPlants = [];
      this.geometryDirty = true;
      this.viewProjection = new Float32Array(16);
      this.inverseViewProjection = null;
      this.rockData = new Float32Array(16 * 4);
      this.lightDirection = new Float32Array([0.0, 0.0, 1.0]);
      this.lightColor = new Float32Array([1.0, 1.0, 1.0]);
      this.shadowColor = new Float32Array([0.20, 0.26, 0.39]);
      this.lightIntensity = 1;

      this.moundProgram = this.createMoundProgram();
      this.moundUniforms = this.getCommonUniforms(this.moundProgram);
      this.moundUniforms.uMossTexture = this.gl.getUniformLocation(
        this.moundProgram,
        'uMossTexture');
      this.fiberProgram = this.createFiberProgram();
      this.fiberUniforms = this.getAnimatedUniforms(this.fiberProgram);
      this.ornamentalProgram = this.createOrnamentalProgram();
      this.ornamentalUniforms = this.getAnimatedUniforms(
        this.ornamentalProgram);
      this.ornamentalUniforms.uShadowPass = this.gl.getUniformLocation(
        this.ornamentalProgram,
        'uShadowPass');
      this.leafyProgram = this.createLeafyProgram();
      this.leafyUniforms = this.getAnimatedUniforms(this.leafyProgram);
      this.leafyUniforms.uPlantTexture = this.gl.getUniformLocation(
        this.leafyProgram,
        'uPlantTexture');
      this.mossTexture = this.createTexture('MossTexture.png', true);
      this.leafyTexture = this.createTexture('LeafyPlantTexture.png');

      this.moundBuffer = null;
      this.fiberBuffer = null;
      this.ornamentalBuffer = null;
      this.flowerBuffer = null;
      this.leafyBuffer = null;
      this.gl.bindVertexArray(this.originalVao);
    }

    compile(type, source) {
      const shader = this.gl.createShader(type);
      this.gl.shaderSource(shader, source);
      this.gl.compileShader(shader);
      if (!this.gl.getShaderParameter(shader, this.gl.COMPILE_STATUS))
        throw new Error(this.gl.getShaderInfoLog(shader));
      return shader;
    }

    linkProgram(vertexSource, fragmentSource) {
      const program = this.gl.createProgram();
      this.gl.attachShader(
        program,
        this.compile(this.gl.VERTEX_SHADER, vertexSource));
      this.gl.attachShader(
        program,
        this.compile(this.gl.FRAGMENT_SHADER, fragmentSource));
      this.gl.linkProgram(program);
      if (!this.gl.getProgramParameter(program, this.gl.LINK_STATUS))
        throw new Error(this.gl.getProgramInfoLog(program));
      return program;
    }

    createTexture(fileName, repeat = false) {
      const gl = this.gl;
      const texture = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texImage2D(
        gl.TEXTURE_2D,
        0,
        gl.RGBA,
        1,
        1,
        0,
        gl.RGBA,
        gl.UNSIGNED_BYTE,
        new Uint8Array([0, 0, 0, 0]));
      gl.texParameteri(
        gl.TEXTURE_2D,
        gl.TEXTURE_WRAP_S,
        repeat ? gl.REPEAT : gl.CLAMP_TO_EDGE);
      gl.texParameteri(
        gl.TEXTURE_2D,
        gl.TEXTURE_WRAP_T,
        repeat ? gl.REPEAT : gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);

      const image = new Image();
      image.addEventListener('load', () => {
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
        gl.texImage2D(
          gl.TEXTURE_2D,
          0,
          gl.RGBA,
          gl.RGBA,
          gl.UNSIGNED_BYTE,
          image);
        gl.generateMipmap(gl.TEXTURE_2D);
        gl.texParameteri(
          gl.TEXTURE_2D,
          gl.TEXTURE_MIN_FILTER,
          gl.LINEAR_MIPMAP_LINEAR);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
      });
      image.src = new URL(fileName, window.location.href).href;
      return texture;
    }

    sharedFragmentHeader() {
      return `
        precision highp float;
        precision highp int;
        uniform vec4 uRocks[16];
        uniform int uRockCount;
        uniform vec3 uLight;
        uniform vec3 uLightColor;
        uniform vec3 uShadowColor;
        uniform float uLightIntensity;

        bool insideRock(vec2 world){
          for (int index = 0; index < 16; index++){
            if (index >= uRockCount) break;
            vec4 rock = uRocks[index];
            vec2 delta = (world - rock.xy) / max(rock.zw, vec2(0.001));
            if (dot(delta, delta) < 0.54) return true;
          }
          return false;
        }

        float rockShadow(vec2 world){
          float shadow = 0.0;
          vec2 shadowDirection = normalize(-uLight.xy + vec2(0.0001));
          float lightReach = clamp(
            length(uLight.xy) / max(uLight.z, 0.15),
            0.35,
            1.65);
          for (int index = 0; index < 16; index++){
            if (index >= uRockCount) break;
            vec4 rock = uRocks[index];
            float footprint = (rock.z + rock.w) * 0.5;
            vec2 center = rock.xy +
              shadowDirection * footprint * (0.30 + lightReach * 0.22);
            vec2 delta = (world - center) /
              max(rock.zw * (1.10 + lightReach * 0.10), vec2(0.001));
            float shaped = 1.0 - smoothstep(0.66, 1.34, length(delta));
            float outsideBody = smoothstep(
              0.50,
              0.92,
              length((world - rock.xy) / max(rock.zw, vec2(0.001))));
            shadow = max(shadow, shaped * outsideBody);
          }
          return shadow;
        }
      `;
    }

    createMoundProgram() {
      const vertexSource = `#version 300 es
        precision highp float;
        layout(location=0) in vec4 aPositionDensity;
        layout(location=1) in vec4 aColorSeed;
        uniform mat4 uViewProjection;
        uniform vec2 uPointer;
        uniform float uPointerStrength;
        uniform vec4 uRocks[16];
        uniform int uRockCount;
        out vec2 vWorld;
        out vec3 vColor;
        out float vDensity;
        out float vHeight;
        out float vKind;

        void main(){
          vec2 world = aPositionDensity.xy;
          float distanceToPointer = distance(world, uPointer);
          float influence =
            1.0 - smoothstep(0.025, 0.135, distanceToPointer);
          influence *= influence * uPointerStrength;
          float compression =
            influence * (0.0015 + aPositionDensity.w * 0.0025);
          float rockCompression = 0.0;
          for (int index = 0; index < 16; index++){
            if (index >= uRockCount) break;
            vec4 rock = uRocks[index];
            vec2 delta = (world - rock.xy) / max(rock.zw, vec2(0.001));
            rockCompression = max(
              rockCompression,
              1.0 - smoothstep(0.78, 1.36, length(delta)));
          }
          float height = max(0.001, aPositionDensity.z - compression);
          height = max(
            0.001,
            height - rockCompression * (0.004 + aPositionDensity.w * 0.008));
          gl_Position = uViewProjection *
            vec4(world.x, 0.002 + height, world.y, 1.0);
          vWorld = world;
          vColor = aColorSeed.rgb;
          vDensity = aPositionDensity.w;
          vHeight = height;
          vKind = aColorSeed.w;
        }`;
      const fragmentSource = `#version 300 es
        ${this.sharedFragmentHeader()}
        uniform sampler2D uMossTexture;
        in vec2 vWorld;
        in vec3 vColor;
        in float vDensity;
        in float vHeight;
        in float vKind;
        out vec4 fragColor;

        float noise(vec2 point){
          return fract(sin(dot(point, vec2(127.1, 311.7))) * 43758.5453);
        }

        void main(){
          if (insideRock(vWorld)) discard;
          float densityWidth = max(fwidth(vDensity) * 1.35, 0.006);
          float edge = smoothstep(
            0.045 - densityWidth,
            0.22 + densityWidth,
            vDensity);
          if (edge < 0.002) discard;
          float broad = 0.5 + 0.5 * sin(
            vWorld.x * 83.0 + sin(vWorld.y * 67.0) * 1.8);
          float fine = noise(floor(vWorld * 780.0 + 7.3));
          float cushion = step(0.5, vKind);
          float volumeLight = mix(
            1.0,
            0.97 + vDensity * 0.15 + vHeight * 0.90,
            cushion);
          float cushionTexture = mix(
            1.0,
            0.93 + fine * 0.105 + broad * 0.025,
            cushion);
          vec3 color = vColor *
            (0.78 + broad * 0.07 + fine * 0.055 + vHeight * 3.2) *
            volumeLight * cushionTexture;
          vec4 mossA = texture(
            uMossTexture,
            fract(vWorld * vec2(4.8, 5.3) + vec2(0.13, 0.27)));
          vec4 mossB = texture(
            uMossTexture,
            fract(vWorld.yx * vec2(7.1, 6.4) + vec2(0.61, 0.19)));
          float mossMask = max(mossA.a, mossB.a * 0.72);
          vec3 mossDetail = mix(mossA.rgb, mossB.rgb, mossB.a * 0.48);
          float mossLuminance = dot(
            mossDetail,
            vec3(0.2126, 0.7152, 0.0722));
          vec3 tintedDetail =
            vColor * (0.58 + mossLuminance * 1.28);
          color = mix(
            color,
            tintedDetail * (0.78 + vDensity * 0.32),
            mossMask * 0.42);
          float colorPatch = clamp(
            broad * 0.68 + mossLuminance * 0.32,
            0.0,
            1.0);
          float colorGrain = fine;
          vec3 mossTint = mix(
            vec3(0.88, 1.055, 0.89),
            vec3(1.09, 1.015, 0.80),
            colorPatch);
          color *= mix(
            vec3(1.0),
            mossTint,
            0.18 + colorGrain * 0.12);
          vec2 slope = vec2(
            cos(vWorld.x * 24.0 + broad * 2.4),
            cos(vWorld.y * 21.0 - broad * 2.1)) *
            mix(0.08, 0.22, cushion);
          vec3 mossNormal = normalize(vec3(-slope, 1.0));
          float diffuse = max(dot(mossNormal, normalize(uLight)), 0.0);
          float illumination =
            0.34 + diffuse * 0.72 * uLightIntensity;
          color *= illumination;
          color *= mix(vec3(1.0), uLightColor, 0.62);
          float castShadow = rockShadow(vWorld);
          color = mix(
            color,
            uShadowColor,
            castShadow * (0.30 + uLightIntensity * 0.08));
          vec2 densityGradient = vec2(
            dFdx(vDensity),
            dFdy(vDensity));
          vec2 outward = -densityGradient /
            max(length(densityGradient), 0.00001);
          vec2 horizontalLight = normalize(uLight.xy + vec2(0.00001));
          float edgeFacingLight = dot(outward, horizontalLight);
          float edgeBand = 1.0 - smoothstep(0.12, 0.88, edge);
          float lightReach = clamp(
            length(uLight.xy) / max(uLight.z, 0.16),
            0.10,
            1.45);
          float edgeShadow = edgeBand *
            (0.025 + (0.5 - edgeFacingLight * 0.5) *
              lightReach * (0.12 + uLightIntensity * 0.08));
          color = mix(
            color,
            uShadowColor,
            clamp(edgeShadow, 0.0, 0.34));
          float edgeHighlight = edgeBand *
            max(edgeFacingLight, 0.0) *
            lightReach * uLightIntensity * 0.055;
          color *= 1.0 + edgeHighlight;
          fragColor = vec4(color, edge * mix(0.98, 0.97, cushion));
        }`;
      return this.linkProgram(vertexSource, fragmentSource);
    }

    createFiberProgram() {
      const vertexSource = `#version 300 es
        precision highp float;
        layout(location=0) in vec4 aRootScale;
        layout(location=1) in vec4 aVariation;
        layout(location=2) in vec4 aColor;
        uniform mat4 uViewProjection;
        uniform vec2 uPointer;
        uniform vec2 uPointerVelocity;
        uniform float uPointerStrength;
        uniform float uTime;
        uniform float uAspect;
        uniform vec4 uRocks[16];
        uniform int uRockCount;
        out vec2 vWorld;
        out vec3 vColor;
        out float vAlong;
        out float vTone;

        void main(){
          int corner = gl_VertexID;
          bool tip = corner == 2 || corner == 3 || corner == 5;
          bool right = corner == 1 || corner == 4 || corner == 5;
          float along = tip ? 1.0 : 0.0;
          float across = right ? 1.0 : -1.0;
          float angle = aVariation.x;
          vec2 direction = vec2(cos(angle) * uAspect, sin(angle));
          vec2 side = vec2(-sin(angle) * uAspect, cos(angle));
          vec2 root = aRootScale.xy;
          vec2 away = root - uPointer;
          float pointerDistance = length(away);
          vec2 awayDirection = pointerDistance > 0.0001
            ? away / pointerDistance
            : vec2(0.0);
          float influence =
            1.0 - smoothstep(0.015, 0.16, pointerDistance);
          influence *= influence * uPointerStrength;
          vec2 brushedDirection = normalize(
            awayDirection * 0.78 +
            normalize(uPointerVelocity + vec2(0.0001)) * 0.22);
          float breeze = sin(
            uTime * 0.55 + aVariation.y * 6.2831853 +
            root.x * 11.0 + root.y * 8.0);
          vec2 lean =
            brushedDirection * influence * aRootScale.w * 0.34 +
            direction * breeze * aRootScale.w * 0.035;
          vec2 rockLean = vec2(0.0);
          for (int index = 0; index < 16; index++){
            if (index >= uRockCount) break;
            vec4 rock = uRocks[index];
            vec2 delta = root - rock.xy;
            float normalizedDistance = length(
              delta / max(rock.zw, vec2(0.001)));
            float rockInfluence =
              1.0 - smoothstep(0.82, 1.42, normalizedDistance);
            rockLean += normalize(delta + vec2(0.0001)) *
              rockInfluence * aRootScale.w * 0.68;
          }
          lean += rockLean;
          float fiberLength =
            aRootScale.w * (0.40 + aVariation.y * 0.34);
          float fiberWidth =
            aRootScale.w * (0.045 + aVariation.z * 0.025);
          float widthTaper = mix(1.0, 0.12, along);
          vec2 world = root +
            direction * fiberLength * (along - 0.42) +
            side * fiberWidth * widthTaper * across +
            lean * along;
          float height = aRootScale.z +
            aRootScale.w * (0.18 + aVariation.y * 0.16) -
            influence * aRootScale.w * 0.22 * along;
          gl_Position = uViewProjection *
            vec4(world.x, 0.003 + height, world.y, 1.0);
          vWorld = world;
          vColor = aColor.rgb;
          vAlong = along;
          vTone = aVariation.z;
        }`;
      const fragmentSource = `#version 300 es
        ${this.sharedFragmentHeader()}
        in vec2 vWorld;
        in vec3 vColor;
        in float vAlong;
        in float vTone;
        out vec4 fragColor;

        void main(){
          if (insideRock(vWorld)) discard;
          float illumination =
            0.42 + max(uLight.z, 0.0) * 0.62 * uLightIntensity;
          vec3 color = vColor *
            mix(0.86, 1.16, smoothstep(0.08, 0.72, vTone)) *
            mix(0.92, 1.10, vAlong) *
            illumination *
            mix(vec3(1.0), uLightColor, 0.58);
          float castShadow = rockShadow(vWorld);
          color = mix(
            color,
            uShadowColor,
            castShadow * (0.34 + uLightIntensity * 0.08));
          float edgeCoverage = smoothstep(0.42, 0.68, vTone);
          fragColor = vec4(
            color,
            mix(0.24, 0.48, vAlong) * mix(0.38, 1.0, edgeCoverage));
        }`;
      return this.linkProgram(vertexSource, fragmentSource);
    }

    createOrnamentalProgram() {
      const vertexSource = `#version 300 es
        precision highp float;
        layout(location=0) in vec4 aRootSize;
        layout(location=1) in vec4 aShape;
        layout(location=2) in vec4 aColor;
        uniform mat4 uViewProjection;
        uniform vec2 uPointer;
        uniform vec2 uPointerVelocity;
        uniform float uPointerStrength;
        uniform float uTime;
        uniform float uAspect;
        uniform vec3 uLight;
        uniform int uShadowPass;
        out vec2 vWorld;
        out vec3 vColor;
        out float vAlong;
        out float vAcross;
        out float vOpacity;
        out float vFlowerPart;

        void main(){
          bool flowerGeometry = gl_VertexID >= 18;
          int localVertex = flowerGeometry ? gl_VertexID - 18 : gl_VertexID;
          int segment = localVertex / 6;
          int corner = localVertex - segment * 6;
          bool useEnd = corner == 2 || corner == 3 || corner == 5;
          bool useRight = corner == 1 || corner == 4 || corner == 5;
          float along = flowerGeometry
            ? 1.0
            : (float(segment) + (useEnd ? 1.0 : 0.0)) / 3.0;
          float across = useRight ? 1.0 : -1.0;
          vec2 direction = vec2(cos(aShape.x) * uAspect, sin(aShape.x));
          vec2 side = vec2(-sin(aShape.x) * uAspect, cos(aShape.x));
          float curve = aShape.y * sin(along * 3.1415927);
          float reach = aRootSize.z * (
            along * 0.32 + along * along * 0.68);
          vec2 root = aRootSize.xy;
          vec2 away = root - uPointer;
          float pointerDistance = length(away);
          vec2 awayDirection = pointerDistance > 0.0001
            ? away / pointerDistance
            : vec2(0.0);
          float influence =
            1.0 - smoothstep(0.025, 0.22, pointerDistance);
          influence *= influence * uPointerStrength;
          float tipResponse = pow(along, 1.65);
          float breeze = sin(
            uTime * 0.78 + aShape.z * 6.2831853 +
            root.x * 7.0 + root.y * 5.0);
          vec2 bendDirection = vec2(
            awayDirection.x * uAspect,
            awayDirection.y);
          vec2 motionDirection = normalize(
            vec2(uPointerVelocity.x, uPointerVelocity.y) + vec2(0.0001));
          vec2 bend =
            bendDirection * influence * aRootSize.z * 0.11 * tipResponse +
            motionDirection *
              influence * aRootSize.z * 0.025 * tipResponse +
            side * breeze * aRootSize.z * 0.012 * tipResponse;
          float opacity = abs(aShape.w);
          float hasFlower = step(0.0, -aShape.w);
          if (flowerGeometry){
            if (hasFlower < 0.5){
              gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
              vWorld = vec2(0.0);
              vColor = vec3(0.0);
              vAlong = 0.0;
              vAcross = 0.0;
              vOpacity = 0.0;
              vFlowerPart = -1.0;
              return;
            }

            int flowerPart = localVertex / 6;
            int flowerCorner = localVertex - flowerPart * 6;
            bool flowerEnd =
              flowerCorner == 2 || flowerCorner == 3 || flowerCorner == 5;
            bool flowerRight =
              flowerCorner == 1 || flowerCorner == 4 || flowerCorner == 5;
            float flowerAlong = flowerEnd ? 1.0 : 0.0;
            float flowerAcross = flowerRight ? 1.0 : -1.0;
            vec2 world = root + direction * aRootSize.z + bend;
            float flowerStyle = fract(
              aShape.z * 3.731 + root.x * 5.17 + root.y * 7.93);
            bool threePetals = flowerStyle < 0.46;
            if (threePetals && flowerPart == 3){
              gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
              vWorld = vec2(0.0);
              vColor = vec3(0.0);
              vAlong = 0.0;
              vAcross = 0.0;
              vOpacity = 0.0;
              vFlowerPart = -1.0;
              return;
            }
            float bloomRadius = max(
              aRootSize.z * mix(0.038, 0.055, flowerStyle),
              0.0024);
            if (flowerPart < 4){
              float petalCount = threePetals ? 3.0 : 4.0;
              float petalAngle =
                float(flowerPart) * 6.2831853 / petalCount +
                aShape.z * 6.2831853;
              vec2 petalDirection =
                vec2(cos(petalAngle) * uAspect, sin(petalAngle));
              vec2 petalSide =
                vec2(-sin(petalAngle) * uAspect, cos(petalAngle));
              float petalWidth = mix(
                0.31,
                0.46,
                fract(flowerStyle * 6.19 + float(flowerPart) * 0.27));
              world +=
                petalDirection * bloomRadius *
                  mix(0.08, 1.02, flowerAlong) +
                petalSide * bloomRadius * petalWidth * flowerAcross;
              vColor = mix(
                vec3(0.98, 0.96, 0.84),
                vec3(1.0, 0.88, 0.91),
                smoothstep(0.32, 0.88, flowerStyle));
              vFlowerPart = 1.0;
            } else {
              world += vec2(
                flowerAcross * uAspect,
                flowerAlong * 2.0 - 1.0) * bloomRadius * 0.34;
              vColor = mix(
                vec3(0.96, 0.73, 0.24),
                vec3(0.91, 0.58, 0.20),
                flowerStyle);
              vFlowerPart = 2.0;
            }
            float height =
              max(0.0, sin(2.42)) * aColor.a +
              aColor.a * 0.14 -
              influence * aColor.a * 0.08;
            if (uShadowPass == 1){
              vec2 lightDirection = normalize(uLight.xy + vec2(0.0001));
              float shadowReach =
                height * length(uLight.xy) / max(uLight.z, 0.15) * 0.82;
              world -= lightDirection * shadowReach;
              height = 0.0;
            }
            gl_Position = uViewProjection *
              vec4(world.x, 0.005 + height, world.y, 1.0);
            vWorld = world;
            vAlong = flowerAlong;
            vAcross = flowerAcross;
            vOpacity = opacity;
            return;
          }
          float width = aRootSize.w * 0.5 *
            mix(1.0, 0.16, smoothstep(0.18, 1.0, along));
          if (uShadowPass == 1)
            width *= 1.35 + along * 0.55;
          vec2 world = root +
            direction * reach +
            side * (curve * aRootSize.z + width * across) +
            bend;
          float arch = sin(along * 2.42);
          float height =
            max(0.0, arch) * aColor.a +
            along * aColor.a * 0.14 -
            influence * aColor.a * 0.08 * tipResponse;
          if (uShadowPass == 1){
            vec2 lightDirection = normalize(uLight.xy + vec2(0.0001));
            float shadowReach =
              height * length(uLight.xy) / max(uLight.z, 0.15) * 0.82;
            world -= lightDirection * shadowReach;
            height = 0.0;
          }
          gl_Position = uViewProjection *
            vec4(world.x, 0.004 + height, world.y, 1.0);
          vWorld = world;
          vColor = aColor.rgb;
          vAlong = along;
          vAcross = across;
          vOpacity = opacity;
          vFlowerPart = 0.0;
        }`;
      const fragmentSource = `#version 300 es
        ${this.sharedFragmentHeader()}
        in vec2 vWorld;
        in vec3 vColor;
        in float vAlong;
        in float vAcross;
        in float vOpacity;
        in float vFlowerPart;
        uniform int uShadowPass;
        out vec4 fragColor;

        void main(){
          if (vFlowerPart < -0.5) discard;
          if (insideRock(vWorld)) discard;
          if (vFlowerPart > 0.5){
            vec2 flowerUv = vec2(vAcross, vAlong * 2.0 - 1.0);
            float edge = 1.0 - smoothstep(
              0.70,
              1.0,
              length(flowerUv));
            if (edge < 0.01) discard;
            if (uShadowPass == 1){
              fragColor = vec4(
                uShadowColor,
                edge * 0.13 * vOpacity);
              return;
            }
            float illumination =
              0.58 + max(uLight.z, 0.0) * 0.52 * uLightIntensity;
            vec3 color = vColor *
              illumination *
              mix(vec3(1.0), uLightColor, 0.42);
            fragColor = vec4(color, edge * 0.96 * vOpacity);
            return;
          }
          if (uShadowPass == 1){
            float widthFade =
              1.0 - smoothstep(0.12, 1.0, abs(vAcross));
            float rootFade = smoothstep(0.0, 0.10, vAlong);
            float distanceFade = mix(0.82, 0.30, vAlong);
            fragColor = vec4(
              uShadowColor,
              widthFade * rootFade * distanceFade * 0.14 * vOpacity);
            return;
          }
          float centerLight = 1.0 - abs(vAcross) * 0.10;
          vec3 color = vColor *
            mix(0.90, 1.28, vAlong) * centerLight;
          fragColor = vec4(color, 0.93 * vOpacity);
        }`;
      return this.linkProgram(vertexSource, fragmentSource);
    }

    createLeafyProgram() {
      const vertexSource = `#version 300 es
        precision highp float;
        layout(location=0) in vec4 aRootSize;
        layout(location=1) in vec4 aShape;
        layout(location=2) in vec4 aColor;
        uniform mat4 uViewProjection;
        uniform vec2 uPointer;
        uniform vec2 uPointerVelocity;
        uniform float uPointerStrength;
        uniform float uTime;
        uniform float uAspect;
        out vec2 vWorld;
        out vec2 vUv;
        out vec3 vTint;
        out float vOpacity;

        void main(){
          bool top = gl_VertexID == 2 ||
            gl_VertexID == 3 || gl_VertexID == 5;
          bool right = gl_VertexID == 1 ||
            gl_VertexID == 4 || gl_VertexID == 5;
          vec2 uv = vec2(right ? 1.0 : 0.0, top ? 1.0 : 0.0);
          vec2 local = uv * 2.0 - 1.0;
          vec2 direction = vec2(cos(aShape.x) * uAspect, sin(aShape.x));
          vec2 side = vec2(-sin(aShape.x) * uAspect, cos(aShape.x));
          vec2 root = aRootSize.xy;
          vec2 away = root - uPointer;
          float pointerDistance = length(away);
          vec2 awayDirection = pointerDistance > 0.0001
            ? away / pointerDistance
            : vec2(0.0);
          float influence =
            1.0 - smoothstep(0.02, 0.18, pointerDistance);
          influence *= influence * uPointerStrength;
          float edgeResponse = smoothstep(0.18, 1.0, length(local));
          float breeze = sin(
            uTime * 0.62 + aShape.z * 6.2831853 +
            root.x * 8.0 + root.y * 6.0);
          vec2 bend = vec2(
            awayDirection.x * uAspect,
            awayDirection.y) *
              influence * aRootSize.z * 0.07 * edgeResponse +
            side * breeze * aRootSize.z * 0.007 * edgeResponse;
          vec2 world = root +
            side * local.x * aRootSize.z +
            direction * local.y * aRootSize.w +
            bend;
          float dome = max(0.0, 1.0 - dot(local, local) * 0.58);
          float height = dome * aColor.a -
            influence * aColor.a * 0.04 * edgeResponse;
          gl_Position = uViewProjection *
            vec4(world.x, 0.006 + height, world.y, 1.0);
          vWorld = world;
          vUv = uv;
          vTint = aColor.rgb;
          vOpacity = aShape.w;
        }`;
      const fragmentSource = `#version 300 es
        ${this.sharedFragmentHeader()}
        uniform sampler2D uPlantTexture;
        in vec2 vWorld;
        in vec2 vUv;
        in vec3 vTint;
        in float vOpacity;
        out vec4 fragColor;

        void main(){
          if (insideRock(vWorld)) discard;
          vec4 plant = texture(uPlantTexture, vUv);
          if (plant.a < 0.025) discard;
          float tintLight = dot(vTint, vec3(0.2126, 0.7152, 0.0722));
          fragColor = vec4(
            plant.rgb * mix(0.92, 1.08, tintLight),
            plant.a * vOpacity);
        }`;
      return this.linkProgram(vertexSource, fragmentSource);
    }

    getCommonUniforms(program) {
      const names = [
        'uViewProjection',
        'uPointer',
        'uPointerStrength',
        'uRocks[0]',
        'uRockCount',
        'uLight',
        'uLightColor',
        'uShadowColor',
        'uLightIntensity'
      ];
      const uniforms = {};
      for (const name of names)
        uniforms[name] = this.gl.getUniformLocation(program, name);
      return uniforms;
    }

    getAnimatedUniforms(program) {
      const uniforms = this.getCommonUniforms(program);
      uniforms.uPointerVelocity = this.gl.getUniformLocation(
        program,
        'uPointerVelocity');
      uniforms.uTime = this.gl.getUniformLocation(program, 'uTime');
      uniforms.uAspect = this.gl.getUniformLocation(program, 'uAspect');
      return uniforms;
    }

    createVertexBuffer(values, attributes, vertexCount) {
      const vao = this.gl.createVertexArray();
      const buffer = this.gl.createBuffer();
      this.gl.bindVertexArray(vao);
      this.gl.bindBuffer(this.gl.ARRAY_BUFFER, buffer);
      this.gl.bufferData(
        this.gl.ARRAY_BUFFER,
        new Float32Array(values),
        this.gl.STATIC_DRAW);
      const stride = attributes * 4 * Float32Array.BYTES_PER_ELEMENT;
      for (let attribute = 0; attribute < attributes; attribute++) {
        this.gl.enableVertexAttribArray(attribute);
        this.gl.vertexAttribPointer(
          attribute,
          4,
          this.gl.FLOAT,
          false,
          stride,
          attribute * 4 * Float32Array.BYTES_PER_ELEMENT);
      }
      this.gl.bindVertexArray(null);
      return { vao, buffer, count: vertexCount };
    }

    createInstanceBuffer(values, vertexCount) {
      const result = this.createVertexBuffer(
        values,
        3,
        values.length / 12);
      this.gl.bindVertexArray(result.vao);
      for (let attribute = 0; attribute < 3; attribute++)
        this.gl.vertexAttribDivisor(attribute, 1);
      this.gl.bindVertexArray(null);
      result.vertexCount = vertexCount;
      return result;
    }

    createMoundBuffer(aspect) {
      const values = [];
      const targetCellSize = 0.0045;
      const gridSize = (
        length,
        minimum,
        maximum,
        cellSize = targetCellSize
      ) =>
        Math.round(clamp(
          Math.ceil(length / cellSize),
          minimum,
          maximum));

      const pushVertex = (
        island,
        nx,
        nz,
        seed,
        kind = 0,
        sampleOverride = null
      ) => {
        const sample = sampleOverride || islandSample(island, nx, nz);
        const colorMix = clamp(
          sample.density * 0.55 +
          valueNoise(nx * 13.0, nz * 13.0) * 0.30,
          0,
          1);
        values.push(
          nx * aspect,
          nz,
          sample.height,
          sample.density,
          island.color[0] +
            (island.accent[0] - island.color[0]) * colorMix,
          island.color[1] +
            (island.accent[1] - island.color[1]) * colorMix,
          island.color[2] +
            (island.accent[2] - island.color[2]) * colorMix,
          kind
        );
      };

      for (let islandIndex = 0; islandIndex < this.mossIslands.length;
          islandIndex++) {
        const island = this.mossIslands[islandIndex];
        const bounds = islandBounds(island);
        const columns = gridSize(bounds.right - bounds.left, 36, 160);
        const rows = gridSize(bounds.bottom - bounds.top, 32, 144);
        for (let row = 0; row < rows; row++) {
          const z0 = bounds.top +
            (bounds.bottom - bounds.top) * row / rows;
          const z1 = bounds.top +
            (bounds.bottom - bounds.top) * (row + 1) / rows;
          for (let column = 0; column < columns; column++) {
            const x0 = bounds.left +
              (bounds.right - bounds.left) * column / columns;
            const x1 = bounds.left +
              (bounds.right - bounds.left) * (column + 1) / columns;
            const seed = islandIndex * 0.37 +
              row * 0.013 + column * 0.007;
            pushVertex(island, x0, z0, seed);
            pushVertex(island, x1, z0, seed);
            pushVertex(island, x1, z1, seed);
            pushVertex(island, x0, z0, seed);
            pushVertex(island, x1, z1, seed);
            pushVertex(island, x0, z1, seed);
          }
        }
      }

      const random = seededRandom(0xC05A10);
      for (let islandIndex = 0; islandIndex < this.mossIslands.length;
          islandIndex++) {
        const island = this.mossIslands[islandIndex];
        const bounds = islandBounds(island);
        const painted = islandIndex >= MOSS_ISLANDS.length;
        const largestSourceRadius = Math.max(
          ...island.lobes.map(lobe => Math.sqrt(lobe[2] * lobe[3])));
        const targetCount = painted
          ? largestSourceRadius < 0.012
            ? 0
            : clamp(Math.ceil(island.lobes.length / 2), 1, 8)
          : islandIndex === 0 ? 7 : 4;
        let created = 0;
        let attempts = 0;
        while (created < targetCount && attempts < targetCount * 30) {
          attempts++;
          const centerX =
            bounds.left + (bounds.right - bounds.left) * random();
          const centerZ =
            bounds.top + (bounds.bottom - bounds.top) * random();
          const centerSample = islandSample(island, centerX, centerZ);
          if (centerSample.density < (painted ? 0.18 : 0.28)) {
            continue;
          }
          const sourceLobe = island.lobes[
            Math.floor(random() * island.lobes.length)];
          const clusterEdgeBlur = centerSample.edgeBlur;
          const sourceRadius = Math.sqrt(sourceLobe[2] * sourceLobe[3]);
          const clusterRadius = clamp(
            sourceRadius * (0.62 + random() * 0.42),
            painted ? 0.030 : 0.042,
            painted ? 0.078 : 0.086);
          const clusterHeight = clamp(
            sourceLobe[4] *
              (0.90 + random() * 0.62),
            painted ? 0.010 : 0.024,
            0.050);
          const lobeCount = 4 + Math.floor(random() * 3);
          const clusterLobes = [[
            centerX,
            centerZ,
            clusterRadius * (0.72 + random() * 0.18),
            clusterRadius * (0.72 + random() * 0.18),
            clusterHeight
          ]];
          for (let lobeIndex = 1; lobeIndex < lobeCount; lobeIndex++) {
            const angle =
              lobeIndex / (lobeCount - 1) * Math.PI * 2 +
              (random() - 0.5) * 0.75;
            const offset = clusterRadius * (0.28 + random() * 0.30);
            const lobeRadius = clusterRadius * (0.42 + random() * 0.28);
            clusterLobes.push([
              centerX + Math.cos(angle) * offset,
              centerZ + Math.sin(angle) * offset,
              lobeRadius * (0.91 + random() * 0.18),
              lobeRadius * (0.91 + random() * 0.18),
              clusterHeight * (0.72 + random() * 0.30)
            ]);
          }

          let clusterLeft = 1;
          let clusterRight = 0;
          let clusterTop = 1;
          let clusterBottom = 0;
          for (const lobe of clusterLobes) {
            clusterLeft = Math.min(clusterLeft, lobe[0] - lobe[2] * 1.08);
            clusterRight = Math.max(clusterRight, lobe[0] + lobe[2] * 1.08);
            clusterTop = Math.min(clusterTop, lobe[1] - lobe[3] * 1.08);
            clusterBottom = Math.max(
              clusterBottom,
              lobe[1] + lobe[3] * 1.08);
          }

          const sampleCluster = (x, z) => {
            let remaining = 1;
            let height = 0;
            const feather = 0.33 + clusterEdgeBlur * 0.25;
            for (const lobe of clusterLobes) {
              const dx = (x - lobe[0]) / lobe[2];
              const dz = (z - lobe[1]) / lobe[3];
              const density =
                1 - smoothstep(
                  0.71 - feather,
                  0.71 + feather,
                  Math.hypot(dx, dz));
              remaining *= 1 - density;
              height = Math.max(height, density * lobe[4]);
            }
            const rawDensity = 1 - remaining;
            const edgeNoise = (
              valueNoise(
                x * 24 + created * 2.7,
                z * 24 + islandIndex * 4.1) - 0.5) *
              0.10 * (1 - clusterEdgeBlur * 0.82);
            const density = clamp(
              rawDensity + edgeNoise * (1 - rawDensity),
              0,
              1);
            const baseHeight = islandSample(island, x, z).height;
            return {
              density,
              height:
                baseHeight * 0.24 +
                height *
                  (0.90 + valueNoise(x * 11, z * 11) * 0.14) *
                  smoothstep(0.04, 0.70, density)
            };
          };

          const clusterColumns = gridSize(
            clusterRight - clusterLeft,
            28,
            96,
            0.0028);
          const clusterRows = gridSize(
            clusterBottom - clusterTop,
            24,
            88,
            0.0028);
          const clusterSeed = islandIndex * 0.41 + created * 0.073;
          for (let row = 0; row < clusterRows; row++) {
            const z0 = clusterTop +
              (clusterBottom - clusterTop) * row / clusterRows;
            const z1 = clusterTop +
              (clusterBottom - clusterTop) * (row + 1) / clusterRows;
            for (let column = 0; column < clusterColumns; column++) {
              const x0 = clusterLeft +
                (clusterRight - clusterLeft) * column / clusterColumns;
              const x1 = clusterLeft +
                (clusterRight - clusterLeft) * (column + 1) / clusterColumns;
              pushVertex(
                island, x0, z0, clusterSeed, 1, sampleCluster(x0, z0));
              pushVertex(
                island, x1, z0, clusterSeed, 1, sampleCluster(x1, z0));
              pushVertex(
                island, x1, z1, clusterSeed, 1, sampleCluster(x1, z1));
              pushVertex(
                island, x0, z0, clusterSeed, 1, sampleCluster(x0, z0));
              pushVertex(
                island, x1, z1, clusterSeed, 1, sampleCluster(x1, z1));
              pushVertex(
                island, x0, z1, clusterSeed, 1, sampleCluster(x0, z1));
            }
          }
          created++;
        }
      }
      return this.createVertexBuffer(values, 2, values.length / 8);
    }

    createFiberBuffer(aspect) {
      const random = seededRandom(0xB0A57);
      const lowPower = (navigator.hardwareConcurrency || 8) <= 4;
      const desiredPerIsland = lowPower ? [3000, 1700] : [5200, 2900];
      const values = [];

      for (let islandIndex = 0; islandIndex < this.mossIslands.length;
          islandIndex++) {
        const island = this.mossIslands[islandIndex];
        const bounds = islandBounds(island);
        const desired = desiredPerIsland[islandIndex] ??
          Math.min(
            lowPower ? 620 : 900,
            Math.max(180, island.lobes.length * (lowPower ? 110 : 160)));
        let accepted = 0;
        let attempts = 0;
        while (accepted < desired && attempts < desired * 30) {
          attempts++;
          const nx = bounds.left + (bounds.right - bounds.left) * random();
          const nz = bounds.top + (bounds.bottom - bounds.top) * random();
          const sample = islandSample(island, nx, nz);
          if (sample.density < 0.42) continue;
          const colorMix = clamp(
            sample.density * 0.42 + random() * 0.52,
            0,
            1);
          const size = (0.0042 + random() * 0.0036) *
            (0.08 + smoothstep(0.42, 0.68, sample.density) * 0.92);
          values.push(
            nx * aspect,
            nz,
            sample.height,
            size,
            random() * Math.PI * 2,
            random(),
            sample.density,
            islandIndex,
            island.color[0] +
              (island.accent[0] - island.color[0]) * colorMix,
            island.color[1] +
              (island.accent[1] - island.color[1]) * colorMix,
            island.color[2] +
              (island.accent[2] - island.color[2]) * colorMix,
            1
          );
          accepted++;
        }
      }
      return this.createInstanceBuffer(values, 6);
    }

    createOrnamentalBuffer(aspect) {
      const random = seededRandom(0x5ED6E);
      const values = [];
      const flowerValues = [];
      const totalLeaves = this.ornamentalClumps.reduce(
        (total, clump) => total + clump.leaves,
        0);
      const flowerCount = totalLeaves > 0
        ? Math.min(9, Math.max(6, Math.ceil(totalLeaves / 20) + 2))
        : 0;
      const flowerSlots = new Set(
        Array.from(
          { length: flowerCount },
          (_, index) => Math.min(
            totalLeaves - 1,
            Math.floor((index + 0.62) * totalLeaves / flowerCount))));
      let globalLeafIndex = 0;

      for (let clumpIndex = 0; clumpIndex < this.ornamentalClumps.length;
          clumpIndex++) {
        const clump = this.ornamentalClumps[clumpIndex];
        for (let leaf = 0; leaf < clump.leaves; leaf++) {
          const subCluster = leaf % 3;
          const clusterRotation =
            clump.rotation + (subCluster - 1) * 0.34;
          const fan = (random() - 0.5) * 0.44;
          const angle = clusterRotation + fan;
          const sideAngle = clusterRotation + Math.PI * 0.5;
          const rootSpread = clump.radius * (0.42 + random() * 0.46);
          const clusterOffset = (subCluster - 1) * clump.radius * 0.24;
          const rootX = clump.x +
            Math.cos(sideAngle) *
              ((random() - 0.5) * rootSpread + clusterOffset) +
            Math.cos(clusterRotation) * (random() - 0.5) * rootSpread * 0.35;
          const rootZ = clump.z +
            Math.sin(sideAngle) *
              ((random() - 0.5) * rootSpread + clusterOffset) +
            Math.sin(clusterRotation) * (random() - 0.5) * rootSpread * 0.35;
          const length = clump.length * (0.72 + random() * 0.34);
          const width = clump.radius * (0.072 + random() * 0.042);
          const curve = (random() - 0.5) * 0.18;
          const colorVariation = (random() - 0.5) * 0.055;
          const hasFlower = flowerSlots.has(globalLeafIndex);
          globalLeafIndex++;
          const opacity = Number.isFinite(clump.opacity)
            ? clamp(clump.opacity, 0.05, 1)
            : 1;
          const phase = random();
          const height = clump.height * (0.76 + random() * 0.34);
          const instance = [
            rootX * aspect,
            rootZ,
            length,
            width,
            angle,
            curve,
            phase,
            opacity,
            clamp(clump.color[0] + colorVariation, 0, 1),
            clamp(clump.color[1] + colorVariation * 0.75, 0, 1),
            clamp(clump.color[2] + colorVariation * 0.55, 0, 1),
            height
          ];
          values.push(...instance);
          if (hasFlower){
            const flowerInstance = [...instance];
            flowerInstance[7] = -opacity;
            flowerValues.push(...flowerInstance);
          }
        }
      }
      this.flowerBuffer = this.createInstanceBuffer(flowerValues, 30);
      return this.createInstanceBuffer(values, 18);
    }

    createLeafyBuffer(aspect) {
      const random = seededRandom(0x1EAF51);
      const values = [];
      for (let plantIndex = 0; plantIndex < this.leafyPlants.length;
          plantIndex++) {
        const plant = this.leafyPlants[plantIndex];
        values.push(
          plant.x * aspect,
          plant.z,
          plant.size * 0.78,
          plant.size * 0.69,
          plant.rotation,
          random(),
          0,
          plant.opacity,
          plant.color[0],
          plant.color[1],
          plant.color[2],
          plant.size * 0.22
        );
      }
      return this.createInstanceBuffer(values, 6);
    }

    deleteBuffer(resource) {
      if (!resource) return;
      this.gl.deleteBuffer(resource.buffer);
      this.gl.deleteVertexArray(resource.vao);
    }

    resize(aspect) {
      const aspectChanged = Math.abs(aspect - this.aspect) >= 0.0001;
      if (!aspectChanged && !this.geometryDirty) return;
      if (aspectChanged) {
        this.aspect = aspect;
        this.viewProjection = new Float32Array([
          2 / aspect, 0, 0, 0,
          0, 0.38, -0.08, 0,
          0, 2, -0.40, 0,
          -1, -1, 0.40, 1
        ]);
        this.inverseViewProjection = invert4(this.viewProjection);
      }
      this.deleteBuffer(this.moundBuffer);
      this.deleteBuffer(this.fiberBuffer);
      this.deleteBuffer(this.ornamentalBuffer);
      this.deleteBuffer(this.flowerBuffer);
      this.deleteBuffer(this.leafyBuffer);
      this.moundBuffer = this.createMoundBuffer(aspect);
      this.fiberBuffer = this.createFiberBuffer(aspect);
      this.ornamentalBuffer = this.createOrnamentalBuffer(aspect);
      this.leafyBuffer = this.createLeafyBuffer(aspect);
      this.geometryDirty = false;
    }

    pointerFromScreen(normalizedX, normalizedY, now) {
      if (!this.inverseViewProjection) return null;
      const clipX = normalizedX * 2 - 1;
      const clipY = 1 - normalizedY * 2;
      const near = transform4(
        this.inverseViewProjection,
        [clipX, clipY, -1, 1]);
      const far = transform4(
        this.inverseViewProjection,
        [clipX, clipY, 1, 1]);
      for (const point of [near, far]) {
        point[0] /= point[3];
        point[1] /= point[3];
        point[2] /= point[3];
      }
      const direction = [
        far[0] - near[0],
        far[1] - near[1],
        far[2] - near[2]
      ];
      if (Math.abs(direction[1]) < 0.00001) return null;
      const distance = -near[1] / direction[1];
      const x = near[0] + direction[0] * distance;
      const z = near[2] + direction[2] * distance;
      this.pointerTarget[0] = x;
      this.pointerTarget[1] = z;
      this.lastPointerTime = now;
      return { x, z };
    }

    clearPointer() {
      this.lastPointerTime = -Infinity;
    }

    mossDensityAt(x, z) {
      if (this.aspect <= 0) return 0;
      const nx = x / this.aspect;
      let density = 0;
      for (const island of this.mossIslands)
        density = Math.max(density, islandSample(island, nx, z).density);
      return density;
    }

    ornamentalDensityAt(x, z) {
      if (this.aspect <= 0) return 0;
      const nx = x / this.aspect;
      let density = 0;
      for (const clump of this.ornamentalClumps) {
        const distance = Math.hypot(
          (nx - clump.x) / (clump.length * 0.78),
          (z - clump.z) / (clump.length * 0.68));
        density = Math.max(density, 1 - smoothstep(0.35, 1.0, distance));
      }
      return density;
    }

    leafyDensityAt(x, z) {
      if (this.aspect <= 0) return 0;
      const nx = x / this.aspect;
      let density = 0;
      for (const plant of this.leafyPlants) {
        const distance = Math.hypot(
          (nx - plant.x) / Math.max(plant.size * 0.62, 0.001),
          (z - plant.z) / Math.max(plant.size * 0.56, 0.001));
        density = Math.max(density, 1 - smoothstep(0.30, 1.0, distance));
      }
      return density;
    }

    interactionAt(x, z) {
      const moss = this.mossDensityAt(x, z);
      const ornamental = this.ornamentalDensityAt(x, z);
      const leafy = this.leafyDensityAt(x, z);
      if (leafy > Math.max(moss, ornamental) * 0.85)
        return { type: 'leafy', density: leafy };
      return ornamental > moss * 0.85
        ? { type: 'ornamental', density: ornamental }
        : { type: 'moss', density: moss };
    }

    densityAt(x, z) {
      const interaction = this.interactionAt(x, z);
      return interaction.density;
    }

    setPaintedMossStrokes(strokes, mossColors = []) {
      const paintedIslands = [];
      for (let strokeIndex = 0;
          strokeIndex < (strokes || []).length;
          strokeIndex++) {
        const stroke = strokes[strokeIndex];
        if (!Array.isArray(stroke) || stroke.length === 0) continue;
        const selectedColor = (
          Array.isArray(mossColors[strokeIndex])
            ? mossColors[strokeIndex]
            : [0.33, 0.47, 0.25])
          .slice(0, 3)
          .map(value => clamp(value, 0, 1));
        const lobes = [];
        let opacity = 0.72;
        for (const stamp of stroke) {
          if (!Array.isArray(stamp) || stamp.length < 5) continue;
          const [
            screenX,
            screenY,
            size,
            ,
            alpha,
            ,
            smoothing = 0,
            roundness = 0
          ] = stamp;
          if (![screenX, screenY, size, alpha].every(Number.isFinite))
            continue;
          const radius = clamp(size, 0.006, 0.32);
          const puff = clamp(roundness, 0, 1);
          const footprintRadius = radius * (0.48 - puff * 0.04);
          lobes.push([
            clamp(screenX, -0.1, 1.1),
            clamp(1 - screenY, -0.1, 1.1),
            footprintRadius,
            radius * (0.41 + puff * 0.03),
            0.004 + radius * 0.085 +
              smoothstep(0.012, 0.05, radius) * 0.010 +
              puff * (0.010 + radius * 0.16),
            clamp(smoothing, 0, 1)
          ]);
          opacity = clamp(alpha, 0.2, 1);
        }
        if (lobes.length === 0) continue;
        paintedIslands.push({
          color: selectedColor.map(value =>
            value * (0.72 + opacity * 0.28)),
          accent: [
            clamp(selectedColor[0] * 1.20 + 0.025, 0, 1),
            clamp(selectedColor[1] * 1.16 + 0.035, 0, 1),
            clamp(selectedColor[2] * 1.08 + 0.018, 0, 1)
          ],
          lobes
        });
      }
      this.mossIslands = [
        ...MOSS_ISLANDS.map(island => ({
          ...island,
          lobes: island.lobes.map(lobe => [...lobe])
        })),
        ...paintedIslands
      ];
      this.geometryDirty = true;
    }

    setOrnamentals(clumps) {
      this.ornamentalClumps = (clumps || [])
        .filter(clump =>
          clump &&
          ['x', 'z', 'radius', 'length', 'height', 'rotation', 'leaves']
            .every(key => Number.isFinite(clump[key])) &&
          Array.isArray(clump.color) &&
          clump.color.length >= 3)
        .map(clump => ({
          x: clamp(clump.x, -0.1, 1.1),
          z: clamp(clump.z, -0.1, 1.1),
          radius: clamp(clump.radius, 0.012, 0.09),
          length: clamp(clump.length, 0.025, 0.16),
          height: clamp(clump.height, 0.02, 0.14),
          rotation: clump.rotation,
          color: clump.color.slice(0, 3).map(value => clamp(value, 0, 1)),
          leaves: Math.round(clamp(clump.leaves, 8, 36)),
          opacity: Number.isFinite(clump.opacity)
            ? clamp(clump.opacity, 0.05, 1)
            : 1
        }));
      this.geometryDirty = true;
    }

    setLeafyPlants(plants) {
      this.leafyPlants = (plants || [])
        .filter(plant =>
          plant &&
          ['x', 'z', 'size', 'rotation', 'opacity', 'leaves']
            .every(key => Number.isFinite(plant[key])) &&
          Array.isArray(plant.color) &&
          plant.color.length >= 3)
        .map(plant => ({
          x: clamp(plant.x, -0.1, 1.1),
          z: clamp(plant.z, -0.1, 1.1),
          size: clamp(plant.size, 0.025, 0.18),
          rotation: plant.rotation,
          opacity: clamp(plant.opacity, 0.05, 1),
          leaves: Math.round(clamp(plant.leaves, 5, 10)),
          color: plant.color.slice(0, 3).map(value => clamp(value, 0, 1))
        }));
      this.geometryDirty = true;
    }

    update(now) {
      const dt = Math.min((now - this.lastFrameTime) / 1000, 0.05);
      this.lastFrameTime = now;
      const previousX = this.pointer[0];
      const previousZ = this.pointer[1];
      const smoothing = 1 - Math.exp(-dt * 12);
      this.pointer[0] +=
        (this.pointerTarget[0] - this.pointer[0]) * smoothing;
      this.pointer[1] +=
        (this.pointerTarget[1] - this.pointer[1]) * smoothing;
      const measuredX = (this.pointer[0] - previousX) / Math.max(dt, 0.001);
      const measuredZ = (this.pointer[1] - previousZ) / Math.max(dt, 0.001);
      const velocitySmoothing = 1 - Math.exp(-dt * 8);
      this.pointerVelocity[0] +=
        (measuredX - this.pointerVelocity[0]) * velocitySmoothing;
      this.pointerVelocity[1] +=
        (measuredZ - this.pointerVelocity[1]) * velocitySmoothing;

      const active = now - this.lastPointerTime < 180;
      const targetStrength = active
        ? this.reducedMotion ? 0.35 : 1
        : 0;
      const response = active ? 12 : 3.8;
      this.pointerStrength +=
        (targetStrength - this.pointerStrength) *
        (1 - Math.exp(-dt * response));
      if (!active) {
        const damping = Math.exp(-dt * 6);
        this.pointerVelocity[0] *= damping;
        this.pointerVelocity[1] *= damping;
      }
    }

    setRocks(rocks) {
      const rockCount = Math.min(rocks.length, 16);
      this.rockData.fill(0);
      for (let index = 0; index < rockCount; index++) {
        const rock = rocks[index];
        const radius = rock.radius * (1 + rock.height * 0.45);
        this.rockData[index * 4] = rock.x;
        this.rockData[index * 4 + 1] = rock.y;
        this.rockData[index * 4 + 2] =
          radius * Math.max(rock.sx, 0.72) * 0.96;
        this.rockData[index * 4 + 3] =
          radius * Math.max(rock.sy, 0.72) * 0.96;
      }
      return rockCount;
    }

    applyCommonUniforms(uniforms, rockCount) {
      const gl = this.gl;
      gl.uniformMatrix4fv(
        uniforms.uViewProjection,
        false,
        this.viewProjection);
      gl.uniform2fv(uniforms.uPointer, this.pointer);
      gl.uniform1f(uniforms.uPointerStrength, this.pointerStrength);
      gl.uniform4fv(uniforms['uRocks[0]'], this.rockData);
      gl.uniform1i(uniforms.uRockCount, rockCount);
      gl.uniform3fv(uniforms.uLight, this.lightDirection);
      gl.uniform3fv(uniforms.uLightColor, this.lightColor);
      gl.uniform3fv(uniforms.uShadowColor, this.shadowColor);
      gl.uniform1f(uniforms.uLightIntensity, this.lightIntensity);
    }

    applyAnimatedUniforms(uniforms, rockCount, now) {
      this.applyCommonUniforms(uniforms, rockCount);
      this.gl.uniform2fv(uniforms.uPointerVelocity, this.pointerVelocity);
      this.gl.uniform1f(uniforms.uTime, now * 0.001);
      if (uniforms.uAspect)
        this.gl.uniform1f(uniforms.uAspect, this.aspect);
    }

    render(width, height, aspect, rocks, now, lighting = null) {
      this.resize(aspect);
      this.update(now);
      if (lighting) {
        if (Array.isArray(lighting.direction) ||
            ArrayBuffer.isView(lighting.direction))
          this.lightDirection.set(lighting.direction.slice(0, 3));
        if (Array.isArray(lighting.color) ||
            ArrayBuffer.isView(lighting.color))
          this.lightColor.set(lighting.color.slice(0, 3));
        if (Array.isArray(lighting.shadowColor) ||
            ArrayBuffer.isView(lighting.shadowColor))
          this.shadowColor.set(lighting.shadowColor.slice(0, 3));
        if (Number.isFinite(lighting.intensity))
          this.lightIntensity = clamp(lighting.intensity, 0.1, 2);
      }
      const rockCount = this.setRocks(rocks);
      const gl = this.gl;

      gl.viewport(0, 0, width, height);
      gl.disable(gl.DEPTH_TEST);
      gl.depthMask(false);
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

      gl.useProgram(this.moundProgram);
      this.applyCommonUniforms(this.moundUniforms, rockCount);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, this.mossTexture);
      gl.uniform1i(this.moundUniforms.uMossTexture, 0);
      gl.bindVertexArray(this.moundBuffer.vao);
      gl.drawArrays(gl.TRIANGLES, 0, this.moundBuffer.count);

      gl.useProgram(this.ornamentalProgram);
      this.applyAnimatedUniforms(this.ornamentalUniforms, rockCount, now);
      gl.uniform1i(this.ornamentalUniforms.uShadowPass, 1);
      gl.bindVertexArray(this.ornamentalBuffer.vao);
      gl.drawArraysInstanced(
        gl.TRIANGLES,
        0,
        this.ornamentalBuffer.vertexCount,
        this.ornamentalBuffer.count);
      gl.bindVertexArray(this.flowerBuffer.vao);
      gl.drawArraysInstanced(
        gl.TRIANGLES,
        18,
        this.flowerBuffer.vertexCount,
        this.flowerBuffer.count);

      gl.useProgram(this.fiberProgram);
      this.applyAnimatedUniforms(this.fiberUniforms, rockCount, now);
      gl.bindVertexArray(this.fiberBuffer.vao);
      gl.drawArraysInstanced(
        gl.TRIANGLES,
        0,
        this.fiberBuffer.vertexCount,
        this.fiberBuffer.count);

      gl.useProgram(this.ornamentalProgram);
      this.applyAnimatedUniforms(this.ornamentalUniforms, rockCount, now);
      gl.uniform1i(this.ornamentalUniforms.uShadowPass, 0);
      gl.bindVertexArray(this.ornamentalBuffer.vao);
      gl.drawArraysInstanced(
        gl.TRIANGLES,
        0,
        this.ornamentalBuffer.vertexCount,
        this.ornamentalBuffer.count);
      gl.bindVertexArray(this.flowerBuffer.vao);
      gl.drawArraysInstanced(
        gl.TRIANGLES,
        18,
        this.flowerBuffer.vertexCount,
        this.flowerBuffer.count);

      gl.useProgram(this.leafyProgram);
      this.applyAnimatedUniforms(this.leafyUniforms, rockCount, now);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, this.leafyTexture);
      gl.uniform1i(this.leafyUniforms.uPlantTexture, 0);
      gl.bindVertexArray(this.leafyBuffer.vao);
      gl.drawArraysInstanced(
        gl.TRIANGLES,
        0,
        this.leafyBuffer.vertexCount,
        this.leafyBuffer.count);

      gl.depthMask(true);
      gl.disable(gl.BLEND);
      gl.bindVertexArray(this.originalVao);
    }
  }

  window.GardenVegetationRenderer = GardenVegetationRenderer;
  window.MeadowRenderer = GardenVegetationRenderer;
})();
