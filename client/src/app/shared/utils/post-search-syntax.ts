// Mirrors the directives parsed by server/Damebooru.Processing/Services/Posts/QueryParser.cs; keep both in sync.

export interface SearchValueOption {
  value: string;
  label: string;
  aliases?: readonly string[];
}

export type SearchDirectiveValue =
  | { kind: 'options'; options: readonly SearchValueOption[]; multiple: boolean }
  /** A yes/no filter; `key:false` and `-key:true` both mean excluded. `label` names the included state. */
  | { kind: 'flag'; label: string }
  | { kind: 'number' }
  | { kind: 'text'; placeholder: string }
  | {
    kind: 'sort';
    fields: readonly SearchValueOption[];
    /** Shorthands for a `field:direction` pair. */
    presets: readonly SortPreset[];
    /** The server's order when the query has no sort, as `field:direction`. */
    defaultSort: string;
  };

export interface SortPreset extends SearchValueOption {
  sort: string;
}

export interface SearchDirective {
  key: string;
  aliases: readonly string[];
  label: string;
  description: string;
  negatable: boolean;
  value: SearchDirectiveValue;
  examples: readonly string[];
}

export interface SearchSyntaxSuggestion {
  text: string;
  label: string;
  /** Complete tokens end the word; incomplete ones expect more input, e.g. a value after `key:`. */
  complete: boolean;
}

export interface ActiveSearchValue {
  directive: SearchDirective;
  value: string;
  negated: boolean;
}

export const NUMBER_OPERATORS = ['=', '>', '>=', '<', '<='] as const;
export type NumberOperator = (typeof NUMBER_OPERATORS)[number];

const BOOLEAN_OPTIONS: readonly SearchValueOption[] = [
  { value: 'true', label: 'Yes', aliases: ['yes', 'on', '1'] },
  { value: 'false', label: 'No', aliases: ['no', 'off', '0'] },
];

export const SORT_DIRECTIONS: readonly SearchValueOption[] = [
  { value: 'desc', label: 'Descending' },
  { value: 'asc', label: 'Ascending' },
];

export const SORT_DIRECTIVE = {
  key: 'sort',
  aliases: ['order'],
  label: 'Sort',
  description: 'Sort results. Fields sort ascending unless a direction is given; new and old sort by file date, newest or oldest first. Without a sort, newest files come first.',
  negatable: false,
  value: {
    kind: 'sort',
    fields: [
      { value: 'modified-date', label: 'File date', aliases: ['date', 'file-date', 'file-modified-date'] },
      { value: 'import-date', label: 'Import date', aliases: ['imported-date'] },
      { value: 'tag-count', label: 'Tag count', aliases: ['tagcount', 'tags'] },
      { value: 'size', label: 'File size', aliases: ['size-bytes', 'filesize'] },
      { value: 'width', label: 'Width' },
      { value: 'height', label: 'Height' },
      { value: 'id', label: 'ID' },
    ],
    presets: [
      { value: 'new', label: 'File date, newest first', aliases: ['newest'], sort: 'modified-date:desc' },
      { value: 'old', label: 'File date, oldest first', aliases: ['oldest'], sort: 'modified-date:asc' },
    ],
    defaultSort: 'modified-date:desc',
  },
  examples: ['sort:new', 'sort:tag-count:desc'],
} satisfies SearchDirective;

export const SEARCH_DIRECTIVES: readonly SearchDirective[] = [
  {
    key: 'type',
    aliases: [],
    label: 'Media type',
    description: 'Filter by media type. Combine several with commas.',
    negatable: true,
    value: {
      kind: 'options',
      multiple: true,
      options: [
        { value: 'image', label: 'Image', aliases: ['img'] },
        { value: 'gif', label: 'Animated', aliases: ['animation', 'animated'] },
        { value: 'video', label: 'Video', aliases: ['mp4', 'webm'] },
      ],
    },
    examples: ['type:video', '-type:image,gif'],
  },
  {
    key: 'favorite',
    aliases: ['fav'],
    label: 'Favorite',
    description: 'Show only favorites, or only non-favorites.',
    negatable: true,
    value: { kind: 'flag', label: 'Favorites' },
    examples: ['favorite:true', 'fav:false'],
  },
  {
    key: 'tag-count',
    aliases: ['tagcount'],
    label: 'Tag count',
    description: `Filter by number of tags. Operators: ${NUMBER_OPERATORS.join(', ')}.`,
    negatable: false,
    value: { kind: 'number' },
    examples: ['tag-count:0', 'tag-count:>=5'],
  },
  {
    key: 'filename',
    aliases: ['file'],
    label: 'Filename',
    description: 'Match text in the relative file path. Supports * and ? wildcards.',
    negatable: true,
    value: { kind: 'text', placeholder: 'Path contains… (* and ? wildcards)' },
    examples: ['filename:abc.jpg', 'filename:*wallpaper*', '-filename:tmp'],
  },
  SORT_DIRECTIVE,
];

export function formatSearchDirectiveSyntax(directive: SearchDirective): string {
  const value = directive.value;
  switch (value.kind) {
    case 'options':
      return `${directive.key}:${value.options.map(o => o.value).join(value.multiple ? ',' : '|')}`;
    case 'flag':
      return `${directive.key}:true|false`;
    case 'number':
      return `${directive.key}:[op]N`;
    case 'text':
      return `${directive.key}:TEXT`;
    case 'sort':
      return `${directive.key}:FIELD[:${SORT_DIRECTIONS.map(d => d.value).join('|')}]`;
  }
}

/**
 * Returns the query with the directive token added. Single-value directives replace any existing token,
 * multi-option directives merge into an existing token with the same polarity.
 */
export function applySearchDirective(query: string, directive: SearchDirective, value: string, negated = false): string {
  const prefix = negated ? '-' : '';
  const tokens = query.split(/\s+/).filter(Boolean);
  const parsed = tokens.map(parseDirectiveToken);

  if (isMultiOption(directive)) {
    const index = parsed.findIndex(p => p?.directive === directive && p.negated === negated);
    if (index >= 0) {
      const values = parsed[index]!.value.split(',').filter(Boolean);
      if (!values.some(v => canonicalValue(directive, v) === value)) {
        values.push(value);
      }
      tokens[index] = `${prefix}${directive.key}:${values.join(',')}`;
      return joinTokens(tokens);
    }
  }

  const token = directive.value.kind === 'flag'
    ? `${directive.key}:${!negated}`
    : `${prefix}${directive.key}:${value}`;

  if (directive.value.kind === 'text' || isMultiOption(directive)) {
    return joinTokens(tokens.includes(token) ? tokens : [...tokens, token]);
  }

  // Single-value directives replace their existing token where it stands, so the query doesn't reorder.
  const existing = parsed.findIndex(p => p?.directive === directive);
  if (existing < 0) {
    return joinTokens([...tokens, token]);
  }
  return joinTokens(tokens.flatMap((t, i) => i === existing ? [token] : parsed[i]?.directive === directive ? [] : [t]));
}

/** Switches a value between included and excluded, keeping its position where the syntax allows. */
export function flipSearchDirective(query: string, directive: SearchDirective, value: string, negated: boolean): string {
  if (isMultiOption(directive)) {
    return applySearchDirective(removeSearchDirective(query, directive, value, negated), directive, value, !negated);
  }
  if (directive.value.kind === 'flag') {
    return applySearchDirective(query, directive, value, !negated);
  }

  return joinTokens(query.split(/\s+/).filter(Boolean).map(token => {
    const parsed = parseDirectiveToken(token);
    const matches = parsed?.directive === directive && parsed.negated === negated
      && canonicalValue(directive, parsed.value) === value;
    return !matches ? token : negated ? token.slice(1) : `-${token}`;
  }));
}

/** Returns the query without the given canonical value; multi-option tokens keep their other values. */
export function removeSearchDirective(query: string, directive: SearchDirective, value: string, negated: boolean): string {
  const tokens = query.split(/\s+/).filter(Boolean).flatMap(token => {
    const parsed = parseDirectiveToken(token);
    if (parsed?.directive === directive && directive.value.kind === 'flag') {
      return [];
    }
    if (parsed?.directive !== directive || parsed.negated !== negated) {
      return [token];
    }

    if (!isMultiOption(directive)) {
      return canonicalValue(directive, parsed.value) === value ? [] : [token];
    }

    const remaining = parsed.value.split(',').filter(v => v && canonicalValue(directive, v) !== value);
    return remaining.length > 0 ? [`${negated ? '-' : ''}${directive.key}:${remaining.join(',')}`] : [];
  });

  return joinTokens(tokens);
}

/** Returns the query without any token of the directive, including ones the panel can't parse. */
export function clearSearchDirective(query: string, directive: SearchDirective): string {
  return joinTokens(query.split(/\s+/).filter(token => token && parseDirectiveToken(token)?.directive !== directive));
}

/**
 * Directive values present in the query, canonicalized so aliases match the definitions
 * (`fav:yes` → favorite `true`, `sort:tags` → sort `tag-count:asc`). Unparseable values are skipped.
 */
export function getActiveSearchValues(query: string): ActiveSearchValue[] {
  return query.split(/\s+/).flatMap(token => {
    const parsed = parseDirectiveToken(token);
    if (!parsed) {
      return [];
    }

    const { directive, negated } = parsed;
    if (directive.value.kind === 'flag') {
      const flag = parseBoolean(parsed.value);
      return flag === null ? [] : [{ directive, value: 'true', negated: flag === negated }];
    }

    const rawValues = isMultiOption(directive) ? parsed.value.split(',') : [parsed.value];
    return rawValues.flatMap(raw => {
      const value = canonicalValue(directive, raw);
      return value === null ? [] : [{ directive, value, negated }];
    });
  });
}

/** Suggestions for a partially typed word: directive keys, or values once the key is complete. */
export function getSearchSyntaxSuggestions(word: string): SearchSyntaxSuggestion[] {
  const negated = word.startsWith('-');
  const body = negated ? word.slice(1) : word;
  const prefix = negated ? '-' : '';
  const separatorIndex = indexOfUnescapedColon(body);

  if (separatorIndex < 0) {
    const typed = body.toLowerCase();
    if (typed.length === 0) {
      return [];
    }

    return SEARCH_DIRECTIVES
      .filter(d => (!negated || d.negatable) && [d.key, ...d.aliases].some(k => k.startsWith(typed)))
      .map(d => ({ text: `${prefix}${d.key}:`, label: d.label, complete: false }));
  }

  const directive = findSearchDirective(body.slice(0, separatorIndex));
  if (!directive || (negated && !directive.negatable)) {
    return [];
  }

  const head = `${prefix}${body.slice(0, separatorIndex + 1)}`;
  const typedValue = body.slice(separatorIndex + 1).toLowerCase();
  const value = directive.value;

  switch (value.kind) {
    case 'options': {
      if (!value.multiple) {
        return matchOptions(value.options, typedValue)
          .map(o => ({ text: head + o.value, label: o.label, complete: true }));
      }

      const chosen = typedValue.split(',');
      const last = chosen.pop() ?? '';
      return matchOptions(value.options, last)
        .filter(o => !chosen.includes(o.value))
        .map(o => ({ text: head + [...chosen, o.value].join(','), label: o.label, complete: true }));
    }
    case 'sort': {
      const directionIndex = typedValue.indexOf(':');
      if (directionIndex >= 0) {
        const field = typedValue.slice(0, directionIndex);
        return matchOptions(SORT_DIRECTIONS, typedValue.slice(directionIndex + 1))
          .map(d => ({ text: `${head}${field}:${d.value}`, label: d.label, complete: true }));
      }

      return [
        ...matchOptions(value.presets, typedValue).map(p => ({ text: head + p.value, label: p.label, complete: true })),
        ...matchOptions(value.fields, typedValue).map(f => ({ text: `${head}${f.value}:`, label: f.label, complete: false })),
      ];
    }
    case 'flag':
      return matchOptions(BOOLEAN_OPTIONS, typedValue)
        .map(o => ({ text: head + o.value, label: o.label, complete: true }));
    case 'number':
    case 'text':
      return [];
  }
}

/** True when the word is a directive being typed, so tag lookups are pointless. */
export function isSearchDirectiveWord(word: string): boolean {
  const body = word.startsWith('-') ? word.slice(1) : word;
  const separatorIndex = indexOfUnescapedColon(body);
  return separatorIndex > 0 && findSearchDirective(body.slice(0, separatorIndex)) !== undefined;
}

function findSearchDirective(key: string): SearchDirective | undefined {
  const normalized = key.toLowerCase();
  return SEARCH_DIRECTIVES.find(d => d.key === normalized || d.aliases.includes(normalized));
}

function parseDirectiveToken(token: string): { directive: SearchDirective; negated: boolean; value: string } | null {
  const negated = token.startsWith('-');
  const body = negated ? token.slice(1) : token;
  const separatorIndex = indexOfUnescapedColon(body);
  if (separatorIndex <= 0) {
    return null;
  }

  const directive = findSearchDirective(body.slice(0, separatorIndex));
  return directive ? { directive, negated, value: body.slice(separatorIndex + 1) } : null;
}

function isMultiOption(directive: SearchDirective): boolean {
  return directive.value.kind === 'options' && directive.value.multiple;
}

/** Maps a raw token value to the value the definitions use; null when the server would not parse it. */
function canonicalValue(directive: SearchDirective, raw: string): string | null {
  const typed = raw.trim().toLowerCase();
  const value = directive.value;
  switch (value.kind) {
    case 'options':
      return findOption(value.options, typed)?.value ?? null;
    case 'sort': {
      const preset = value.presets.find(p => p.value === typed || p.aliases?.includes(typed));
      if (preset) {
        return preset.sort;
      }

      const [fieldPart, directionPart, ...rest] = typed.split(':');
      const field = findOption(value.fields, fieldPart);
      const direction = directionPart === undefined ? 'asc' : findOption(SORT_DIRECTIONS, directionPart)?.value;
      return field && direction && rest.length === 0 ? `${field.value}:${direction}` : null;
    }
    case 'flag': {
      const flag = parseBoolean(raw);
      return flag === null ? null : String(flag);
    }
    case 'number': {
      const filter = parseNumberFilter(raw);
      return filter ? `${filter.operator}${filter.value}` : null;
    }
    case 'text':
      return raw.length > 0 ? raw : null;
  }
}

export function parseNumberFilter(raw: string): { operator: NumberOperator; value: number } | null {
  const match = /^(>=|<=|>|<|=)?\s*(\d+)$/.exec(raw.trim());
  return match ? { operator: (match[1] ?? '=') as NumberOperator, value: Number(match[2]) } : null;
}

function parseBoolean(raw: string): boolean | null {
  const option = findOption(BOOLEAN_OPTIONS, raw.trim().toLowerCase());
  return option ? option.value === 'true' : null;
}

function findOption(options: readonly SearchValueOption[], typed: string): SearchValueOption | undefined {
  return options.find(o => o.value === typed || o.aliases?.includes(typed));
}

function matchOptions(options: readonly SearchValueOption[], typed: string): readonly SearchValueOption[] {
  return options.filter(o => [o.value, ...(o.aliases ?? [])].some(v => v.startsWith(typed)));
}

function indexOfUnescapedColon(value: string): number {
  for (let i = 0; i < value.length; i++) {
    if (value[i] === '\\') {
      i++;
    } else if (value[i] === ':') {
      return i;
    }
  }
  return -1;
}

function joinTokens(tokens: string[]): string {
  return tokens.length > 0 ? `${tokens.join(' ')} ` : '';
}
