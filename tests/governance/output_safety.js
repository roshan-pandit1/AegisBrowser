(function () {
  "use strict";

  // Return counts only. The caller must not separately print the input.
  function summarize(lines) {
    if (!Array.isArray(lines) || lines.some(line => typeof line !== "string")) {
      throw new TypeError("Expected an array of strings");
    }
    let flagged = 0;
    for (const line of lines) {
      if (/(password|passwd|secret|token|cookie|authorization|api[-_]?key|private[-_ ]?key)/i.test(line) ||
          /https?:\/\/[^\s/?#]+[^\s?#]*\?\S+/i.test(line) ||
          /https?:\/\/[^\s/@]+@/i.test(line)) {
        flagged++;
      }
    }
    return { total: lines.length, flagged, other: lines.length - flagged };
  }

  return Object.freeze({ summarize });
})
