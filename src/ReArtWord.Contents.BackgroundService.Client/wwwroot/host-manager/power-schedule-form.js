import { node, input, select, checkbox, field, action } from './dom.js';

/** 一つの予定を、保存するまで実行へ反映しない入力カードにします。
 * @returns 予定値を読み出す編集行です。
 */
function scheduleRow(ctx, definition, changed, remove)
{
    const root = node('section', '', 'hm-section'), grid = node('div', '', 'hm-grid');
    const kind = select([['wake', ctx.t('powerSchedule.wake')], ['shutdown', ctx.t('powerSchedule.shutdown')]], definition.action);
    const time = input(definition.time, 'time'), zone = input(definition.timeZoneId);
    time.step = '1';
    zone.placeholder = 'UTC / Asia/Tokyo';
    const enabled = checkbox(ctx.t('powerSchedule.enabled'), definition.enabled);
    const mode = select([['daily', ctx.t('powerSchedule.daily')], ['weekdays', ctx.t('powerSchedule.weekdays')]], definition.weekdays?.length ? 'weekdays' : 'daily');
    const days = node('div', '', 'hm-actions'), dayInputs = [];
    for (let day = 0; day < 7; day++)
    {
        const pick = checkbox(ctx.t('powerSchedule.day' + day), definition.weekdays?.includes(day) ?? false);
        dayInputs.push(pick.control);
        days.append(pick.root);
    }

    /** 毎日指定では、曜日の状態を予定値へ含めません。 */
    function renderDays()
    {
        const daily = 'daily';
        days.hidden = mode.value === daily;
    }

    root.addEventListener('input', changed);
    root.addEventListener('change', changed);
    mode.onchange = renderDays;
    grid.append(field(ctx.t('Action'), kind), field(ctx.t('powerSchedule.time'), time), field(ctx.t('powerSchedule.zone'), zone), field(ctx.t('Repeat'), mode));
    root.append(enabled.root, grid, days, action(ctx, 'powerSchedule.remove', 'trash', remove));
    renderDays();
    return { root,

        /** 編集した値だけを、同じ予定IDで取り出します。
         * @returns サーバーの時刻検査へ渡す予定です。
         */
        read()
        {
            const weekdays = 'weekdays';
            const selected = dayInputs.flatMap((pick, day) => pick.checked ? [day] : []);
            if (!time.value || !zone.value.trim() || mode.value === weekdays && !selected.length)
            {
                throw new Error('powerSchedule.invalid');
            }
            return { id: definition.id, action: kind.value, time: time.value, timeZoneId: zone.value.trim(),
                weekdays: mode.value === weekdays ? selected : [], enabled: enabled.control.checked };
        } };
}

/** 個別編集と一括編集で同じ予定入力・件数上限を使用します。
 * @returns 入力欄と明示保存する値です。
 */
export function powerScheduleForm(ctx, definitions, changed)
{
    const root = node('div'), rowsRoot = node('div');
    let rows = [];

    /** 一行を追加し、削除も未保存の変更として扱います。 */
    function append(definition)
    {
        const row = scheduleRow(ctx, definition, changed, () =>
        {
            rows = rows.filter(item => item !== row);
            row.root.remove();
            changed();
        });
        rows.push(row);
        rowsRoot.append(row.root);
    }

    /** 読取り済みの値で入力欄を置き換えます。 */
    function reset(values)
    {
        rows = [];
        rowsRoot.replaceChildren();
        values.forEach(append);
    }

    const add = action(ctx, 'powerSchedule.add', 'plus', () =>
    {
        if (root.inert)
        {
            return;
        }
        if (32 <= rows.length)
        {
            throw new Error('powerSchedule.invalid');
        }
        append({ id: crypto.randomUUID(), action: 'wake', time: '08:00', timeZoneId: 'UTC', weekdays: [], enabled: true });
        changed();
    });
    root.append(rowsRoot, add);
    reset(definitions);
    return { root, reset,

        read: () => rows.map(row => row.read()),

        /** 読取り待ち・保存待ちでは編集を止めます。 */
        setDisabled(value)
        {
            root.inert = value;
            add.disabled = value;
        } };
}
