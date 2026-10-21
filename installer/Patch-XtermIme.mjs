// Reproducible local fix for the pinned MIT-licensed xterm.js 6.0.0 bundle.
// The native-browser regression suite verifies emitted input, not these source markers.
import { readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
const file = new URL('../src/ReArtWord.Contents.BackgroundService.Client/wwwroot/vendor/xterm/xterm.js', import.meta.url);
let source = readFileSync(file, 'utf8');
const marker = '// Clutch-CS local IME ordering patch v1';
if (source.includes(marker))
{
    process.stdout.write('xterm IME patch already applied.\n');
    process.exit(0);
}
const expected = '14903579ff54664cd72f8e8699e6961a6272c21863ec1c3b118cdc8af5d4a972';
if (createHash('sha256').update(source).digest('hex') !== expected)
{
    throw Error('Review the IME integration before patching another xterm bundle.');
}
const start = source.indexOf('compositionstart(){'), end = source.indexOf('updateCompositionElements(e){', start);
if (start < 0 || end < start)
{
    throw Error('Composition helper not found.');
}
const methods = String.raw`
compositionstart(){
  this._flushComposition();
  this._textareaChangePending=false;
  this._isComposing=true;
  const value=this._textarea.value;
  const start=this._textarea.selectionStart??value.length,end=this._textarea.selectionEnd??start;
  this._compositionPosition={start,end};
  this._compositionRange={prefix:value.slice(0,start),suffix:value.slice(end)};
  this._compositionView.textContent='';this._dataAlreadySent='';this._compositionView.classList.add('active');
}
compositionupdate(event){
  this._compositionView.textContent=event.data;
  this.updateCompositionElements();
}
compositionend(){this._finalizeComposition(true);}
keydown(event){
  if(this._isComposing){
    if([20,229,16,17,18].includes(event.keyCode))return false;
    this._finalizeComposition(false);
  }else{
    // A following key/new composition must not overtake a committed IME value.
    this._flushComposition();
  }
  if(event.keyCode===229){this._handleAnyTextareaChanges();return false;}
  this._textareaChangePending=false;
  return true;
}
_finalizeComposition(wait){
  this._compositionView.classList.remove('active');this._isComposing=false;
  if(!this._pendingComposition)this._pendingComposition=this._compositionRange;
  this._isSendingComposition=!!this._pendingComposition;
  clearTimeout(this._compositionTimer);
  if(!wait){this._flushComposition();return;}
  const pending=this._pendingComposition;
  this._compositionTimer=setTimeout(()=>{
    if(this._pendingComposition===pending)this._flushComposition();
  },0);
}
_flushComposition(){
  const range=this._pendingComposition;
  if(!range)return;
  clearTimeout(this._compositionTimer);this._pendingComposition=undefined;this._isSendingComposition=false;
  const value=this._textarea.value;
  // TSF may replace the entire textarea; a vanished prefix must not become a reversed substring.
  const start=value.startsWith(range.prefix)?range.prefix.length:0;
  const end=range.suffix&&value.endsWith(range.suffix)?value.length-range.suffix.length:value.length;
  const text=value.slice(start,Math.max(start,end));
  if(text)this._coreService.triggerDataEvent(text,true);
}
_handleAnyTextareaChanges(){
  // Consume the actual input event once, rather than timers diffing a growing historical textarea.
  this._textareaChangePending=true;
}
handleInput(event){
  if(this._isComposing||event.isComposing||!this._textareaChangePending)return false;
  this._textareaChangePending=false;
  let text;
  if(event.inputType==='insertText')text=event.data;
  else if(event.inputType==='deleteContentBackward')text=String.fromCharCode(127);
  else if(event.inputType==='insertLineBreak'||event.inputType==='insertParagraph')text=String.fromCharCode(13);
  else return false;
  if(text)this._coreService.triggerDataEvent(text,true);
  return true;
}
`;
source = source.slice(0, start) + methods + source.slice(end);
const input = '_inputEvent(e){';
if (source.split(input).length !== 2)
{
    throw Error('Input dispatcher not found.');
}
source = source.replace(input, input + 'if(this._compositionHelper.handleInput(e))return this.cancel(e);');
writeFileSync(file, source + '\n' + marker + '\n');
process.stdout.write('Applied xterm IME ordering patch.\n');
