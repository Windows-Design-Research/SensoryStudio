const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.join(__dirname, '..');
const garden = path.join(root, 'Assets', 'ZenGarden');
const removed = ['dry_realistic_grass_lods_1', 'game_ready_grass'];
const html = fs.readFileSync(path.join(garden, 'zen-garden.html'), 'utf8');
const host = fs.readFileSync(path.join(root, 'src', 'SensoryStudio.WinUI',
  'Experiences', 'ZenGardenExperience.xaml.cs'), 'utf8');
const renderer = fs.readFileSync(path.join(garden, 'meadow-renderer.js'), 'utf8');
for (const name of removed) {
  assert.equal(fs.existsSync(path.join(garden, name)), false, `${name} must not ship`);
  for (const source of [html, host, renderer])
    assert.equal(source.includes(name), false, `${name} must have no runtime references`);
}
assert.doesNotMatch(host, /gameReadyGrass/);
assert.match(host, /UseFantasyGrass = true/);
assert.match(html, /<script src="meadow-renderer\.js"><\/script>/);
assert.match(renderer, /window\.GardenVegetationRenderer = GardenVegetationRenderer/);
for (const file of ['MossTexture.png', 'LeafyPlantTexture.png',
  '06292385-4718-40ec-9d70-94a01dc00c3d.png',
  'TinyFlower5.png', 'TinyFlower6.png', 'TinyFlower7.png', 'TinyFlower8.png'])
  assert.ok(fs.statSync(path.join(garden, file)).size > 0, `${file} must remain`);

const start = html.indexOf('const PLANTS_ENABLED =');
const end = html.indexOf('const grassSprites =', start);
assert.ok(start >= 0 && end > start, 'Real sprite source selection must be found');
const spriteSource = html.slice(start, end);
for (const [meadow, hostScale] of [[true, 0.68], [false, 0.68],
  [true, undefined], [false, undefined]]) {
  let generated = 0;
  const context = new Proxy({}, {
    get: (_, name) => name === 'createLinearGradient'
      ? () => ({ addColorStop() {} })
      : () => {},
    set: () => true,
  });
  const result = vm.runInNewContext(`${spriteSource}
    ({ sources: fantasyGrassSources, scale: FANTASY_GRASS_HEIGHT_SCALE })`, {
    USE_LEGACY_PLANTS: false,
    USE_FANTASY_GRASS: true,
    USE_MEADOW_VEGETATION: meadow,
    window: {
      __gardenAssets: {},
      __gardenFeatures: { fantasyGrassHeightScale: hostScale },
    },
    document: { createElement: () => ({
      getContext: () => context,
      toDataURL: () => `data:image/png;base64,${++generated}`,
    }) },
  });
  assert.equal(generated, meadow ? 0 : 12);
  assert.equal(result.sources.length, generated);
  assert.ok(result.sources.every(source => source.startsWith('data:image/png;')));
  assert.equal(result.scale, hostScale ?? 1.35);
}

function files(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry =>
    entry.isDirectory()
      ? files(path.join(directory, entry.name)).map(file => path.join(entry.name, file))
      : [entry.name]).sort();
}
if (process.argv[2]) {
  const copied = path.resolve(process.argv[2], 'Assets', 'ZenGarden');
  for (const name of removed)
    assert.equal(fs.existsSync(path.join(copied, name)), false, `${name} must not remain in output`);
  assert.deepEqual(files(copied), files(garden), 'Copied garden must have exactly the source files');
  const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  for (const file of files(garden))
    assert.equal(hash(path.join(copied, file)), hash(path.join(garden, file)), file);
  console.log(`Passed exact file inventory and SHA-256 checks for ${copied}`);
}
console.log('Passed grass removal, retained textures, meadow selection and procedural fallback checks.');
