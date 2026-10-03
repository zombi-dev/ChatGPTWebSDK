import { mkdir, readFile, writeFile, copyFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { deflateSync } from "node:zlib";

const source = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(source, "../..");
const version = (await readFile(path.join(root, "VERSION"), "utf8")).trim();
if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error("VERSION must contain a Chromium-compatible three-part release version.");
const output = path.join(root, "artifacts", "extensions");

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const name = Buffer.from(type);
  const size = Buffer.alloc(4); size.writeUInt32BE(data.length);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(Buffer.concat([name, data])));
  return Buffer.concat([size, name, data, crc]);
}

function icon(size) {
  const pixels = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const offset = y * (size * 4 + 1) + 1 + x * 4;
    // Green chat bubble on a dark background; generated locally, no external assets.
    const bubble = x >= size * .18 && x < size * .82 && y >= size * .20 && y < size * .68 ||
      x >= size * .23 && x < size * .42 && y >= size * .68 && y < size * .80 - (x / size - .23) * .5;
    const line = x >= size * .30 && x < size * .70 &&
      (y >= size * .34 && y < size * .40 || y >= size * .48 && y < size * .54);
    pixels.set(line ? [255, 255, 255, 255] : bubble ? [16, 163, 127, 255] : [32, 33, 35, 255], offset);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), pngChunk("IHDR", ihdr),
    pngChunk("IDAT", deflateSync(pixels)), pngChunk("IEND", Buffer.alloc(0))]);
}

// A deterministic standard ZIP (also the XPI format). Only the explicit files below enter a release.
function zip(files) {
  const chunks = [], directory = [];
  let offset = 0;
  for (const [name, data] of files) {
    const filename = Buffer.from(name), crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(0x800, 6);
    local.writeUInt16LE(0x21, 12); local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(filename.length, 26);
    chunks.push(local, filename, data);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x800, 8); central.writeUInt16LE(0x21, 14); central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20); central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(filename.length, 28); central.writeUInt32LE(offset, 42);
    directory.push(central, filename);
    offset += local.length + filename.length + data.length;
  }
  const central = Buffer.concat(directory), end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(files.length, 8); end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(central.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...chunks, central, end]);
}

await mkdir(output, { recursive: true });
for (const browser of ["chromium", "firefox"]) {
  const manifest = JSON.parse(await readFile(path.join(source, "manifest." + browser + ".json"), "utf8"));
  if (manifest.version !== version) throw new Error("Bump both extension manifest versions to match VERSION.");
  const files = [["manifest.json", Buffer.from(JSON.stringify(manifest, null, 2) + "\n")]];
  for (const name of ["auth.js", "background.js"]) files.push([name, await readFile(path.join(source, name))]);
  for (const size of [16, 32, 48, 128]) files.push(["icons/" + size + ".png", icon(size)]);
  files.push(["LICENSE", await readFile(path.join(root, "LICENSE"))]);
  const unpacked = path.join(output, browser);
  await mkdir(path.join(unpacked, "icons"), { recursive: true });
  for (const [name, data] of files) await writeFile(path.join(unpacked, name), data);
  const archive = path.join(output, "chatgpt-web-sdk-auth-" + browser + "-" + version + ".zip");
  await writeFile(archive, zip(files));
  if (browser === "firefox") await copyFile(archive, path.join(output, "chatgpt-web-sdk-auth-firefox-" + version + ".xpi"));
  console.log("Built " + browser + " extension " + version);
}
