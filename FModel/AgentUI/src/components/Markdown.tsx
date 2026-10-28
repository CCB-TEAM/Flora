import { memo, useMemo } from "react";
import { Marked } from "marked";
import DOMPurify from "dompurify";

const marked = new Marked({ gfm: true, breaks: true, async: false });

// links open outside the panel (Flora hands them to the browser)
DOMPurify.addHook("afterSanitizeAttributes", (node) => {
  if (node.tagName === "A") {
    node.setAttribute("target", "_blank");
    node.setAttribute("rel", "noopener noreferrer");
  }
});

/** Model output rendered as Markdown; the HTML is sanitized, model text is never trusted. */
export const Markdown = memo(function Markdown({ text }: { text: string }) {
  const html = useMemo(() => DOMPurify.sanitize(marked.parse(text) as string, { FORBID_TAGS: ["style", "img"] }), [text]);
  return <div className="markdown" dangerouslySetInnerHTML={{ __html: html }} />;
});
