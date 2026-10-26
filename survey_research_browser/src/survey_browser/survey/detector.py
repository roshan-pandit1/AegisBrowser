from __future__ import annotations
from typing import TYPE_CHECKING
if TYPE_CHECKING:
    from playwright.async_api import Page

from .models import Question, Discovery

DISCOVER = r"""() => {
 document.querySelectorAll('[data-srb-field]').forEach(e=>e.removeAttribute('data-srb-field'));
 const visible = e => !!(e.getClientRects().length) && getComputedStyle(e).visibility !== 'hidden';
 const label = e => (e.labels?.[0]?.textContent || e.getAttribute('aria-label') ||
  (e.getAttribute('aria-labelledby') || '').split(' ').map(id=>document.getElementById(id)?.textContent||'').join(' ') ||
  e.closest('fieldset')?.querySelector('legend')?.textContent || e.name || e.id || '').trim();
 const all = [...document.querySelectorAll('input,select,textarea')].filter(e => visible(e) && !e.disabled && !e.readOnly);
 const questions = [], warnings = [], seen = new Set();
 for (const e of all) {
  if (seen.has(e)) continue;
  const type = e.tagName === 'SELECT' ? 'select' : e.tagName === 'TEXTAREA' ? 'textarea' : e.type;
  if (['hidden','button','submit','reset'].includes(type)) continue;
  if (['password','file'].includes(type)) {warnings.push('Sensitive or file field requires manual handling'); continue;}
  const kinds = {text:'text',email:'text',url:'text',tel:'text',search:'text',textarea:'textarea',radio:'radio',checkbox:'checkbox',select:'select',number:'number',date:'date',range:'rating'};
  if (!kinds[type] || (type==='select' && e.multiple)) {warnings.push('Unsupported native field'); continue;}
  const group = ['radio','checkbox'].includes(type) && e.name
    ? all.filter(x=>x.type===type && x.name===e.name && x.form===e.form) : [e];
  const index = questions.length;
  group.forEach(x=>{seen.add(x); x.setAttribute('data-srb-field', String(index));});
  const key = e.name || e.id || label(e);
  if (!key) {warnings.push('Unlabelled field cannot be mapped'); continue;}
  questions.push({key, label: (group.length>1 ? e.closest('fieldset')?.querySelector('legend')?.textContent : '') || label(e),
    kind:kinds[type], selector:'[data-srb-field="'+index+'"]', required:group.some(x=>x.required),
    options:type==='select' ? [...e.options].filter(o=>!o.disabled).map(o=>o.value) : ['radio','checkbox'].includes(type) ? group.map(x=>x.value) : [],
    minimum:e.min || '', maximum:e.max || ''});
 }
 if (questions.some(q=>q.options.length !== new Set(q.options).size)) warnings.push('Ambiguous duplicate option values');
 const duplicates = questions.filter(q=>questions.filter(other=>other.key===q.key).length>1);
 if (duplicates.length) warnings.push('Ambiguous duplicate question keys');
 if ([...document.querySelectorAll('[role=combobox],[role=slider],[role=radio],[role=checkbox]')].some(e=>visible(e) && !['INPUT','SELECT'].includes(e.tagName)))
  warnings.push('Unsupported custom widget: configure native test fixtures or handle manually');
 if ([...document.querySelectorAll('iframe')].some(visible)) warnings.push('Embedded survey frames are not automatically filled');
 return {questions, warnings};
}"""

async def discover(page: Page, next_selector: str) -> Discovery:
    data = await page.evaluate(DISCOVER)
    questions = [Question(**item) for item in data['questions']]
    next_buttons = page.locator(next_selector).filter(visible=True)
    submit_buttons = page.locator('button[type=submit], input[type=submit], button:not([type]), [data-survey-submit="true"]').filter(visible=True)
    next_count = await next_buttons.count()
    if next_count > 1:
        data['warnings'].append('Multiple next buttons are ambiguous')
    submit_count = await submit_buttons.count()
    import re
    if not next_count and await page.get_by_role('button', name=re.compile(r'^(next|continue|next page)$', re.I)).count():
        data['warnings'].append('Next-like button requires an explicit next-page selector in Settings')
    if not next_count and submit_count > 1:
        data['warnings'].append('Multiple submit buttons are ambiguous')
    return Discovery(questions, data['warnings'],
                     next_selector if next_count == 1 else None,
                     'button[type=submit], input[type=submit], button:not([type]), [data-survey-submit="true"]' if submit_count == 1 else None)
