import en from "./strings.en.json" with { type: "json" };

// User-facing text lives in strings.en.json so it can be localized. English is the only table for now.
const table: Record<string, string> = en;

export type StringKey = keyof typeof en;

/** Looks up a message and fills {0}, {1}, ... with the arguments. An unknown key returns the key itself. */
export function t(key: StringKey, ...args: Array<string | number>): string {
    let text = table[key] ?? key;
    args.forEach((arg, index) => {
        text = text.split(`{${index}}`).join(String(arg));
    });
    return text;
}
