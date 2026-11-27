(function (policy) {
  "use strict";
  let passed = 0;
  function check(condition) {
    if (!condition) throw new Error("Output-safety regression at case " + (passed + 1));
    passed++;
  }

  const checkOne = line => policy.summarize([line]);
  check(policy.summarize([]).total === 0);
  check(checkOne("ordinary diagnostic").flagged === 0);
  check(checkOne("PASSWORD" + ": [example]").flagged === 1);
  check(checkOne("authori" + "zation: [example]").flagged === 1);
  check(checkOne("https://example.invalid/path?" + "example").flagged === 1);
  check(checkOne("https://example.invalid/path").flagged === 0);
  check(checkOne("https://" + "example@" + "example.invalid/path").flagged === 1);
  check(checkOne("ordinary diagnostic").other === 1);
  check(!JSON.stringify(checkOne("unique-example-marker")).includes("unique-example-marker"));
  let rejected = false;
  try { policy.summarize([42]); } catch (error) { rejected = error instanceof TypeError; }
  check(rejected);
  return { executed: passed, passed, failed: 0 };
})
