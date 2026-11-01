# WFP policy lifetime: research gates

**Not executed:** no OS flow-attribution experiment or packet-blocking test has run in the required isolated two-VM environment. A receiver's nonce establishes an observation at that receiver; it does not identify a Windows process or a WFP decision.

See [packet-decision attribution research](wfp-packet-attribution.md) for limitations of WFP event correlation and the requirements for any future isolated test.

Microsoft states that [objects created in a dynamic WFP session are removed when the session ends](https://learn.microsoft.com/en-us/windows/win32/fwp/object-management). The prior object-only test observed this cleanup. If a browser outlives that session, the dynamic restriction can disappear first. A watchdog may detect and terminate the browser later but does not prove that no traffic escapes during the interval. Durable restrictive policies, broker lifecycle, permissions, recovery, and BFE restart behavior require separate architecture and disposable-environment testing. Do not install persistent policy or claim fail-closed behavior in this milestone.
