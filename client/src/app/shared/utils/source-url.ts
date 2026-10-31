export interface SourceDescription {
  label: string;
  /** Identifies the post behind the URL, so different URL forms of one post can be merged. */
  key: string;
}

interface SourcePattern {
  hosts: RegExp;
  describe: (url: URL) => SourceDescription | null;
}

const pathMatch = (url: URL, pattern: RegExp) => pattern.exec(url.pathname)?.[1] ?? null;

const SOURCE_PATTERNS: SourcePattern[] = [
  {
    hosts: /(^|\.)danbooru\.donmai\.us$/,
    describe: url => withId('Danbooru', pathMatch(url, /^\/posts?(?:\/show)?\/(\d+)/)),
  },
  {
    hosts: /(^|\.)gelbooru\.com$/,
    describe: url => withId('Gelbooru', url.searchParams.get('id')),
  },
  {
    hosts: /(^|\.)(yande\.re|konachan\.(com|net))$/,
    describe: url => withId(url.hostname.replace(/^www\./, ''), pathMatch(url, /^\/post\/show\/(\d+)/)),
  },
  {
    hosts: /(^|\.)(x|twitter|fxtwitter|vxtwitter|fixupx)\.com$/,
    describe: url => {
      const match = /^\/([^/]+)\/status\/(\d+)/.exec(url.pathname);
      return match ? { label: `X · @${match[1]}`, key: `x:${match[2]}` } : null;
    },
  },
  {
    hosts: /(^|\.)pixiv\.net$/,
    describe: url => withId('Pixiv', pathMatch(url, /\/artworks\/(\d+)/) ?? url.searchParams.get('illust_id')),
  },
  {
    hosts: /(^|\.)pximg\.net$/,
    describe: url => withId('Pixiv', pathMatch(url, /\/(\d+)_p\d+/)),
  },
];

function withId(site: string, id: string | null): SourceDescription | null {
  return id ? { label: `${site} #${id}`, key: `${site.toLowerCase()}:${id}` } : null;
}

/**
 * A short, human-readable label for a source URL, like "Danbooru #123" or "X · @user", plus a key that is the same for
 * different URL forms of the same post. Unknown sites fall back to domain and last path segment, keyed by the URL itself.
 */
export function describeSourceUrl(rawUrl: string): SourceDescription {
  let url: URL;
  try {
    url = new URL(rawUrl);
  } catch {
    return { label: rawUrl, key: rawUrl };
  }

  const host = url.hostname.toLowerCase();
  for (const pattern of SOURCE_PATTERNS) {
    if (pattern.hosts.test(host)) {
      const description = pattern.describe(url);
      if (description) {
        return description;
      }
    }
  }

  const domain = host.replace(/^www\./, '');
  const lastSegment = url.pathname.split('/').filter(Boolean).pop();
  return { label: lastSegment ? `${domain} · ${safeDecode(lastSegment)}` : domain, key: rawUrl };
}

function safeDecode(segment: string): string {
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}
