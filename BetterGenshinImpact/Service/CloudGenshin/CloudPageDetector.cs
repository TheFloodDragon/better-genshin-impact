using BetterGenshinImpact.Service.CloudGenshin.Browser;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.CloudGenshin;

/// <summary>
/// DOM 仅用于云平台外壳。游戏本身的就绪状态由图像识别确认。
/// 类名/标题依据官网公开 web.2847f177.js 与 home.f604732a.js 的组件；
/// 其他布局使用可见语义回退，网页更新导致不确定时停止自动点击。
/// </summary>
public sealed class CloudPageDetector
{
    public async Task<CloudPageSnapshot> InspectAsync(ICloudBrowser browser, CancellationToken cancellationToken)
    {
        var value = await browser.EvaluateAsync(ProbeScript, cancellationToken).ConfigureAwait(false);
        return value?.ToObject<CloudPageSnapshot>() ?? throw new IOException("云游戏页面未返回可解析的状态。");
    }

    public static bool CanAutoClick(CloudPageSnapshot snapshot)
    {
        if (!snapshot.ActionVerified || snapshot.ActionBounds?.IsInside(snapshot.ViewportWidth, snapshot.ViewportHeight) != true) return false;
        return (snapshot.State, snapshot.Action) is
            (CloudPageState.Lobby, CloudPageAction.StartGame) or
            (CloudPageState.Lobby, CloudPageAction.DismissReward) or
            (CloudPageState.QueueSelection, CloudPageAction.SelectNormalQueue) or
            (CloudPageState.Connecting, CloudPageAction.ConfirmEnter);
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
          const result = { state:'Unknown', action:'None', actionVerified:false, message:'等待云原神页面加载或人工处理',
            viewportWidth:innerWidth, viewportHeight:innerHeight, devicePixelRatio:devicePixelRatio, streamReady:false };
          // 浏览器刚启动时窗口处于 about:blank，导航完成前不能判定为离站。
          if (location.protocol === 'about:' || location.protocol === 'data:' || !location.hostname) {
            result.state='Navigating'; result.message='正在打开云原神页面'; return result;
          }
          if (location.protocol !== 'https:' || location.hostname !== 'ys.mihoyo.com' || !/^\/cloud(?:\/|$)/.test(location.pathname)) {
            // 米哈游账号域是登录流程的正常跳转，不是会话中断。
            if (location.protocol === 'https:' && /(^|\.)(mihoyo\.com|miyoushe\.com)$/.test(location.hostname)) {
              result.state='WaitingForLogin'; result.message='请在独立浏览器中完成登录，登录后会自动返回云原神'; return result;
            }
            result.state='Disconnected'; result.message='页面已离开官方云原神'; return result;
          }
          const text = el => (el?.innerText || '').replace(/\s+/g,' ').trim();
          const visible = el => {
            if (!el) return false;
            const r=el.getBoundingClientRect(), s=getComputedStyle(el);
            return r.width>0 && r.height>0 && r.right>0 && r.bottom>0 && r.left<innerWidth && r.top<innerHeight &&
              s.visibility!=='hidden' && s.display!=='none' && Number(s.opacity)>0;
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
          const action = (state,kind,el,message) => {
            result.state=state; result.message=message;
            if (clickable(el)) { result.action=kind; result.actionBounds=bounds(el); result.actionVerified=true; }
            return result;
          };
          const dialogs=all('[role="dialog"],[aria-modal="true"],.clg-confirm-dialog,.custom-dialog,.van-dialog');
          // 只按普通队列标题选择组件，绝不依赖元素顺序、金币余额或默认高亮项。
          const queueDialog=dialogs.find(el=>el.matches('.clg-coin-prior-choose-dialog') || /请选择排队队列/.test(text(el)));
          if (queueDialog) {
            const normal=all('.coin-prior-choose-item',queueDialog).find(el=>
              text(el.querySelector('.coin-prior-choose-item-main__desc__main'))==='普通队列');
            return action('QueueSelection','SelectNormalQueue',normal,'选择普通队列；时长消耗以官网规则为准');
          }
          for (const dialog of dialogs) {
            const t=text(dialog);
            if (/时长不足|时长已耗尽|暂无可用时长|免费时长已用完/.test(t)) { result.state='TimeExhausted';result.message='云游戏可用时长不足，请人工处理';return result; }
            if (/维护中|正在维护|维护期间/.test(t)) { result.state='Maintenance';result.message='云原神维护中';return result; }
            if (/登录失效|登录已失效|重新登录|会话已失效/.test(t)) { result.state='WaitingForLogin';result.message='登录已失效，请在浏览器重新登录';return result; }
            if (/连接中断|链接中断|连接超时/.test(t)) { result.state='Disconnected';result.message='云游戏连接已中断';return result; }
            // 每日登陆奖励弹窗自动点击"我知道了"按钮
            if (/每日登[陆录]奖励|免费时长.*?\d+.*?分钟/.test(t)) {
              const confirmBtn=all('button,[role="button"]',dialog).find(el=>/^(我知道了|确定|关闭)$/.test(text(el)));
              if(confirmBtn) return action('Lobby','DismissReward',confirmBtn,'关闭每日登陆奖励弹窗');
            }
            result.state='AgreementRequired';result.message='页面弹窗需要人工确认，自动点击已暂停';return result;
          }
          if (all('iframe[id*="login"],iframe[src*="passport"],iframe[src*="account.mihoyo"]').length) {
            result.state='WaitingForLogin';result.message='请在独立浏览器中完成登录或验证码';return result;
          }
          const queue=all('[class*="queue"],[class*="waiting"]').find(el=>{
            const t=text(el);return t.length<2000 && /排队|队列/.test(t) && /预计|预估|当前位置|排队中|正在排队|前面还有/.test(t);
          });
          if (queue) {
            result.state='Queuing';
            const estimate=text(queue).match(/(?:预计|预估)[^。！？]{0,45}|当前位置[^。！？]{0,25}|正在排队/);
            result.message=estimate ? '正在排队：'+estimate[0] : '正在普通队列中等待';return result;
          }
          const loadingButton=all('.game-loading__btn').find(el=>text(el)==='开始游戏');
          if (loadingButton && all('.game-loading__msg').some(el=>/加载完成/.test(text(el))))
            return action('Connecting','ConfirmEnter',loadingButton,'云端加载完成，准备开始游戏');
          const sources=all('video,canvas').filter(el=>{
            const r=el.getBoundingClientRect();
            if(r.width<640 || r.height<360) return false;
            return el.matches('.game-player__video,#canvas-player') ||
              (el.tagName==='VIDEO' && el.srcObject instanceof MediaStream) ||
              !!el.closest('[class*="player"],[id*="player"]');
          }).sort((a,b)=>{const ar=a.getBoundingClientRect(),br=b.getBoundingClientRect();return br.width*br.height-ar.width*ar.height;});
          const source=sources[0];
          if (source) {
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
            result.state=result.streamReady?'Streaming':'Connecting';result.message='已连接播放器，等待有效游戏画面';return result;
          }
          // 可点击的独立语义按钮回退，排除整页容器以及未完成登录/弹窗/排队场景。
          const start=all('button,[role="button"],.clg-button,.wel-card__content--start,div,span').find(el=>{
            if(!/^(进入游戏|开始游戏)$/.test(text(el)) || !clickable(el)) return false;
            return el.matches('button,[role="button"],.clg-button,.wel-card__content--start') || getComputedStyle(el).cursor==='pointer';
          });
          if(start) return action('Lobby','StartGame',start,'准备进入云原神普通队列');
          if(all('.game-loading,.clg-loading-box').length) { result.state='Connecting';result.message='正在连接云端或加载页面'; }
          return result;
        })()
        """;
}
