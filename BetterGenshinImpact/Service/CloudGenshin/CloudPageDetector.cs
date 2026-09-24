using BetterGenshinImpact.Service.CloudGenshin.Browser;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.CloudGenshin;

/// <summary>
/// DOM 仅用于云平台外壳；游戏主界面必须另经图像识别确认。
/// 组件依据官网 web.2847f177.js、home.f604732a.js 和官方账号 SDK。
/// 页面更新或弹窗含义不明确时暂停输入，不调用私有登录 API。
/// </summary>
public sealed class CloudPageDetector
{
    public async Task<CloudPageSnapshot> InspectAsync(ICloudBrowser browser, CancellationToken cancellationToken)
    {
        var value = await browser.EvaluateAsync(ProbeScript, cancellationToken).ConfigureAwait(false);
        var snapshot = value?.ToObject<CloudPageSnapshot>() ?? throw new IOException("云游戏页面未返回可解析的状态。");
        if (snapshot.State == CloudPageState.WaitingForLogin && snapshot.HasLoginFrame && browser is ICloudLoginInspector inspector)
        {
            var login = await inspector.InspectLoginFrameAsync(LoginProbeScript, cancellationToken).ConfigureAwait(false);
            var state = login?["state"]?.ToObject<CloudPageState>();
            if (state is CloudPageState.WaitingForLogin or CloudPageState.VerificationRequired
                or CloudPageState.AuthorizationRequired or CloudPageState.LoginFailed or CloudPageState.AgreementRequired)
            {
                // 账号子框架仅提供固定状态，不向外转发表单值、二维码、URL 或页面正文。
                return new CloudPageSnapshot
                {
                    State = state.Value, HasLoginFrame = true,
                    Message = login?["message"]?.Value<string>() ?? snapshot.Message,
                    ViewportWidth = snapshot.ViewportWidth, ViewportHeight = snapshot.ViewportHeight,
                    DevicePixelRatio = snapshot.DevicePixelRatio
                };
            }
        }
        return snapshot;
    }

    public static bool CanAutoClick(CloudPageSnapshot snapshot)
    {
        if (!snapshot.ActionVerified || snapshot.HasLoginFrame
            || snapshot.ActionBounds?.IsInside(snapshot.ViewportWidth, snapshot.ViewportHeight) != true) return false;
        return (snapshot.State, snapshot.Action) is
            (CloudPageState.LoginRequired, CloudPageAction.OpenLogin) or
            (CloudPageState.LoginRequired, CloudPageAction.RetryLogin) or
            (CloudPageState.Lobby, CloudPageAction.StartGame) or
            (CloudPageState.Lobby, CloudPageAction.DismissReward) or
            (CloudPageState.QueueSelection, CloudPageAction.SelectNormalQueue) or
            (CloudPageState.Connecting, CloudPageAction.ConfirmEnter) or
            (CloudPageState.RecoverableError, CloudPageAction.RetryConnection);
    }

    public async Task<bool> TryClickActionAsync(ICloudBrowser browser, CloudPageSnapshot expected, CancellationToken cancellationToken)
    {
        if (!CanAutoClick(expected)) return false;
        var current = await InspectAsync(browser, cancellationToken).ConfigureAwait(false);
        if (current.State != expected.State || current.Action != expected.Action || !CanAutoClick(current)) return false;
        var rect = current.ActionBounds!;
        await browser.ClickAsync(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal const string ProbeScript = """
        (() => {
          const result = { state:'Unknown', action:'None', actionVerified:false, hasLoginFrame:false,
            message:'等待云原神页面加载或人工处理', viewportWidth:innerWidth, viewportHeight:innerHeight,
            devicePixelRatio:devicePixelRatio, streamReady:false };
          if (location.protocol === 'about:' || location.protocol === 'data:' || !location.hostname) {
            result.state='Navigating'; result.message='正在打开云原神页面'; return result;
          }
          const url = new URL(location.href);
          if (url.protocol !== 'https:' || url.hostname !== 'ys.mihoyo.com' || !/^\/cloud(?:\/|$)/.test(url.pathname)
              || url.port || url.username || url.password) {
            // 登录会在多个官方域之间跳转，这些都不是会话中断；
            // 只有已知账号域才允许只读探测登录子框架，避免把任意站点当作账号页处理。
            if (url.protocol === 'https:' && !url.port && !url.username && !url.password &&
                /(^|\.)(mihoyo\.com|miyoushe\.com)$/.test(url.hostname)) {
              result.state='WaitingForLogin';
              result.hasLoginFrame=['user.mihoyo.com','account.mihoyo.com','passport.mihoyo.com'].includes(url.hostname);
              result.message='请在官方账号页面完成登录或授权，等待返回云原神'; return result;
            }
            result.state='Disconnected'; result.message='页面已离开官方云原神，已暂停输入'; return result;
          }
          const text = el => (el?.innerText || el?.textContent || '').replace(/\s+/g,' ').trim();
          const visible = el => {
            if (!el) return false;
            const r=el.getBoundingClientRect();
            if(r.width<=0 || r.height<=0 || r.right<=0 || r.bottom<=0 || r.left>=innerWidth || r.top>=innerHeight) return false;
            for(let p=el;p;p=p.parentElement) {
              const s=getComputedStyle(p);
              if(s.visibility==='hidden' || s.display==='none' || Number(s.opacity)===0 || p.getAttribute('aria-hidden')==='true') return false;
            }
            return true;
          };
          const all = (selector, root=document) => [...root.querySelectorAll(selector)].filter(visible);
          const bounds = el => { const r=el.getBoundingClientRect(); return {x:r.x,y:r.y,width:r.width,height:r.height}; };
          const clickable = el => {
            if (!visible(el) || el.disabled || el.getAttribute('aria-disabled')==='true') return false;
            const r=el.getBoundingClientRect();
            if (r.x<0 || r.y<0 || r.right>innerWidth || r.bottom>innerHeight) return false;
            const hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);
            return hit && (hit===el || el.contains(hit));
          };
          const state = (kind,message) => { result.state=kind; result.message=message; return result; };
          const action = (kind,verb,el,message) => {
            state(kind,message);
            if (clickable(el)) { result.action=verb; result.actionBounds=bounds(el); result.actionVerified=true; }
            return result;
          };
          const buttons = root => all('button,[role="button"],.clg-button',root);
          const loginFrames=all('#mihoyo-login-platform-iframe,iframe[id*="login"],iframe[src*="passport"],iframe[src*="user.mihoyo.com"],iframe[src*="account.mihoyo.com"]');
          if (loginFrames.length) {
            result.hasLoginFrame=true;
            return state('WaitingForLogin','请在官方登录窗口完成密码/短信/扫码登录；验证码和手机授权须人工处理');
          }
          if (all('.geetest_panel,.geetest_panel_box,.geetest_holder,.geetest_box,iframe[src*="captcha"]').length)
            return state('VerificationRequired','请人工完成验证码；不会自动提交或重复发送短信');
          const dialogs=all('[role="dialog"],[aria-modal="true"],.clg-confirm-dialog,.custom-dialog,.van-dialog');
          // 从后出现的弹窗开始；任何动作还需命中测试，不能点击被上层弹窗遮挡的按钮。
          for (const dialog of dialogs.reverse()) {
            const t=text(dialog);
            if (/时长不足|时长已耗尽|暂无可用时长|免费时长已用完/.test(t))
              return state('TimeExhausted','云游戏可用时长不足，请人工处理；不会自动充值');
            if (/维护中|正在维护|维护期间/.test(t)) return state('Maintenance','云原神维护中，请稍后再试');
            if (/当前浏览器无法运行|浏览器版本过低|不支持当前浏览器|浏览器不支持/.test(t))
              return state('UnsupportedBrowser','当前浏览器不兼容，请更新 Edge / Chrome 并检查硬件加速和 WebRTC');
            // 仅按普通队列组件的标题选择，不依赖顺序、高亮或付费余额。
            if (dialog.matches('.clg-coin-prior-choose-dialog') || /请选择排队队列/.test(t)) {
              const normal=all('.coin-prior-choose-item',dialog).find(el=>
                text(el.querySelector('.coin-prior-choose-item-main__desc__main'))==='普通队列');
              return action('QueueSelection','SelectNormalQueue',normal,'选择普通队列；时长消耗以官网规则为准');
            }
            if (/扫码成功|确认登录|授权登录/.test(t)) return state('AuthorizationRequired','请在手机或官方页面确认登录授权');
            if (/验证码|安全验证/.test(t)) return state('VerificationRequired','请人工完成验证码或安全验证，不会自动重发');
            if (/实名认证|用户协议|隐私政策|隐私协议|充值|支付|购买|确认使用/.test(t))
              return state('AgreementRequired','协议、实名或付费提示需要人工确认，自动点击已暂停');
            if (/登录失效|登录已失效|重新登录|会话已失效/.test(t)) {
              const retry=buttons(dialog).find(el=>/^(重新登录|去登录|确定|我知道了)$/.test(text(el)));
              return action('LoginRequired','RetryLogin',retry,'登录态已失效，准备重新打开官方登录；仍需人工验证');
            }
            if (/登录失败|密码错误|账号异常|操作频繁|请求过于频繁/.test(t))
              return state('LoginFailed','登录失败或操作受限，请在官方窗口处理；不会反复提交账号');
            if (/网络异常|网络错误|网络连接失败|服务异常|连接中断|链接中断|连接超时|加载失败|请求失败/.test(t)) {
              const retry=buttons(dialog).find(el=>/^(重试|重新连接|重新加载|返回首页|返回大厅)$/.test(text(el)));
              return action('RecoverableError','RetryConnection',retry,'云服务连接异常，等待有限重试或人工处理');
            }
            if (/每日登[陆录]奖励|免费时长.*?\d+.*?分钟/.test(t)) {
              const confirm=buttons(dialog).find(el=>/^(我知道了|确定|关闭)$/.test(text(el)));
              return action('Lobby','DismissReward',confirm,'关闭每日登录奖励提示');
            }
            return state('AgreementRequired','页面弹窗需要人工确认，自动点击已暂停');
          }
          if(all('input[type="password"],input[autocomplete="one-time-code"]').length)
            return state('WaitingForLogin','请在官方页面完成登录或短信验证码');
          const welcome=all('.welcome-wrapper__btn').find(el=>/^(开始游戏|立即体验|登录)$/.test(text(el)));
          if(welcome) return action('LoginRequired','OpenLogin',welcome,'正在打开官方账号登录窗口');
          const login=all('button,[role="button"],.clg-button').find(el=>/^(登录|立即登录|去登录)$/.test(text(el)) && !el.closest('form'));
          if(login) return action('LoginRequired','OpenLogin',login,'正在打开官方账号登录窗口');
          const queue=all('[class*="queue"],[class*="waiting"]').find(el=>{
            const t=text(el); return t.length<2000 && /排队|队列/.test(t) && /预计|预估|当前位置|排队中|正在排队|前面还有/.test(t);
          });
          if(queue) {
            const estimate=text(queue).match(/(?:预计|预估)[^。！？]{0,45}|当前位置[^。！？]{0,25}|正在排队/);
            return state('Queuing',estimate ? '正在排队：'+estimate[0] : '正在普通队列中等待');
          }
          const loadingButton=all('.game-loading__btn').find(el=>text(el)==='开始游戏');
          if(loadingButton && all('.game-loading__msg').some(el=>/加载完成/.test(text(el))))
            return action('Connecting','ConfirmEnter',loadingButton,'云端加载完成，准备开始游戏');
          const sources=all('video,canvas').filter(el=>{
            const r=el.getBoundingClientRect();
            if(r.width<640 || r.height<360) return false;
            return el.matches('.game-player__video,#canvas-player') ||
              (el.tagName==='VIDEO' && el.srcObject instanceof MediaStream) || !!el.closest('[class*="player"],[id*="player"]');
          }).sort((a,b)=>{const ar=a.getBoundingClientRect(),br=b.getBoundingClientRect();return br.width*br.height-ar.width*ar.height;});
          const source=sources[0];
          if(source) {
            const r=bounds(source),s=getComputedStyle(source);
            const w=source.tagName==='VIDEO' ? source.videoWidth : source.width;
            const h=source.tagName==='VIDEO' ? source.videoHeight : source.height;
            if(w>0 && h>0 && (s.objectFit==='contain' || Math.abs(r.width/r.height-w/h)>0.02)) {
              const scale=Math.min(r.width/w,r.height/h),nw=w*scale,nh=h*scale;
              r.x+=(r.width-nw)/2;r.y+=(r.height-nh)/2;r.width=nw;r.height=nh;
            }
            if(r.x>=0 && r.y>=0 && r.x+r.width<=innerWidth+0.01 && r.y+r.height<=innerHeight+0.01) result.gameBounds=r;
            result.streamReady=w>0 && h>0 && (source.tagName!=='VIDEO' || (source.readyState>=2 && !source.ended && !source.paused));
            if(source.tagName==='VIDEO') result.streamTime=Number.isFinite(source.currentTime)?source.currentTime:null;
            return state(result.streamReady?'Streaming':'Connecting','已连接播放器，等待游戏内主界面确认');
          }
          const start=all('.wel-card__content--start,button,[role="button"],.clg-button').find(el=>
            /^(进入游戏|开始游戏)$/.test(text(el)) && clickable(el) &&
            (el.matches('.wel-card__content--start') || all('.user-aid.wel-card__aid').length));
          if(start) return action('Lobby','StartGame',start,'账号已登录，准备进入云原神普通队列');
          if(all('.maintain-wrapper,[class*="maintain"]').some(el=>/维护中|正在维护|维护期间/.test(text(el))))
            return state('Maintenance','云原神维护中，请稍后再试');
          if(all('.game-loading,.clg-loading-box').length) return state('Connecting','正在连接云端或加载页面');
          return result;
        })()
        """;

    // 在官方账号文档的独立执行上下文内只读检查。绝不读取 input.value、Cookie 或二维码链接。
    internal const string LoginProbeScript = """
        (() => {
          const visible = el => {
            if(!el) return false;
            const r=el.getBoundingClientRect();
            if(r.width<=0 || r.height<=0 || r.bottom<=0 || r.right<=0 || r.left>=innerWidth || r.top>=innerHeight) return false;
            for(let p=el;p;p=p.parentElement) {
              const s=getComputedStyle(p);
              if(s.display==='none' || s.visibility==='hidden' || Number(s.opacity)===0) return false;
            }
            return true;
          };
          const all = selector => [...document.querySelectorAll(selector)].filter(visible);
          const result = (state,message) => ({state,message});
          const labels=all('p,span,div,[role="alert"]').filter(el=>el.children.length===0)
            .map(el=>(el.innerText || el.textContent || '').trim()).filter(t=>t.length<200);
          if(all('.geetest_panel,.geetest_panel_box,.geetest_box,iframe[src*="captcha"]').length || labels.some(t=>/请完成.*验证|滑动.*拼图|安全验证|验证码错误/.test(t)))
            return result('VerificationRequired','请人工完成验证码或安全验证；验证失败请在官方窗口重试');
          if(labels.some(t=>/扫码成功|请在手机.*确认|确认登录|授权登录/.test(t)))
            return result('AuthorizationRequired','已扫码或等待授权，请在手机或官方窗口确认登录');
          if(all('.qr-expired').length || labels.some(t=>/二维码已[过失]效|二维码已过期|密码错误|登录失败|操作频繁|账号异常|验证码已过期/.test(t)))
            return result('LoginFailed','二维码/验证码已过期或登录失败，请在官方窗口刷新或修正后重试');
          if(labels.some(t=>/实名认证|同意.*(?:用户协议|隐私)|授权确认/.test(t)))
            return result('AgreementRequired','请人工确认官方协议、实名或授权要求');
          return result('WaitingForLogin','等待完成官方账号登录；支持密码、短信或扫码，登录成功后自动继续');
        })()
        """;
}
