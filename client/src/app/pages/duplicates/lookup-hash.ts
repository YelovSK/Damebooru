import xxhash from 'xxhash-wasm';

const CONTENT_HASH_CHUNK_SIZE = 64 * 1024;

let xxhashApiPromise: Promise<Awaited<ReturnType<typeof xxhash>>> | null = null;

function getXxhashApi() {
  xxhashApiPromise ??= xxhash();
  return xxhashApiPromise;
}

function writeLittleEndianInt64(value: number): Uint8Array {
  const buffer = new ArrayBuffer(8);
  const view = new DataView(buffer);
  view.setBigUint64(0, BigInt(value), true);
  return new Uint8Array(buffer);
}

export async function computeLookupContentHash(file: File): Promise<string> {
  const { create64 } = await getXxhashApi();
  const hasher = create64();

  hasher.update(writeLittleEndianInt64(file.size));

  const firstChunk = new Uint8Array(await file.slice(0, CONTENT_HASH_CHUNK_SIZE).arrayBuffer());
  hasher.update(firstChunk);

  if (file.size > CONTENT_HASH_CHUNK_SIZE * 2) {
    const lastChunk = new Uint8Array(
      await file.slice(file.size - CONTENT_HASH_CHUNK_SIZE, file.size).arrayBuffer(),
    );
    hasher.update(lastChunk);
  }

  return hasher.digest().toString(16).padStart(16, '0');
}
