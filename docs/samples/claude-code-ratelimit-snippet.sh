#!/usr/bin/env bash
# squirrel-notifier 連携: claude-code の statusLine JSON からレートリミット状態を
# 共通スキーマ（schemaVersion 1、usedPercentage 対応）に変換してローカルファイルへ
# 書き出す（#139、#145）。
#
# 使い方: 既存の statusline スクリプト（例: ~/.claude/statusline-command.sh）で
# `input=$(cat)` により stdin の JSON を読み込んだ直後に、以下の関数呼び出しを追記する。
#   write_squirrel_notifier_ratelimit_status "$input"
#
# 依存: jq, GNU date（Git for Windows の bash に同梱）
#
# 注意: statusline はインタラクティブセッションの表示機構であり、`claude -p` 等の
# ヘッドレス実行では発火しない。そのためヘッドレス実行の前後で squirrel-notifier が
# Delta（レビューサイクル単位の使用率差分）を算出できるとは限らない。Delta は
# best-effort であり、「取得不可」は正常系として扱われる（詳細は ../statusline-integration.md を参照）。

write_squirrel_notifier_ratelimit_status() {
  local input="$1"
  local out_dir="${LOCALAPPDATA}/SquirrelNotifier/ratelimit-status"
  mkdir -p "$out_dir" 2>/dev/null || return 0

  local five_reset seven_reset five_pct seven_pct limits="[]"
  five_reset=$(echo "$input" | jq -r '.rate_limits.five_hour.resets_at // empty' 2>/dev/null)
  seven_reset=$(echo "$input" | jq -r '.rate_limits.seven_day.resets_at // empty' 2>/dev/null)
  five_pct=$(echo "$input" | jq -r '.rate_limits.five_hour.used_percentage // empty' 2>/dev/null)
  seven_pct=$(echo "$input" | jq -r '.rate_limits.seven_day.used_percentage // empty' 2>/dev/null)

  if [ -n "$five_reset" ]; then
    local five_iso
    five_iso=$(date -u -d "@${five_reset}" '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null)
    if [ -n "$five_iso" ]; then
      limits=$(echo "$limits" | jq --arg id "claude-code-5h" --arg label "claude-code 5時間枠" --arg reset "$five_iso" --argjson pct "${five_pct:-null}" \
        '. + [{"id":$id,"label":$label,"resetAt":$reset,"usedPercentage":$pct}]')
    fi
  fi

  if [ -n "$seven_reset" ]; then
    local seven_iso
    seven_iso=$(date -u -d "@${seven_reset}" '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null)
    if [ -n "$seven_iso" ]; then
      limits=$(echo "$limits" | jq --arg id "claude-code-7d" --arg label "claude-code 週次枠" --arg reset "$seven_iso" --argjson pct "${seven_pct:-null}" \
        '. + [{"id":$id,"label":$label,"resetAt":$reset,"usedPercentage":$pct}]')
    fi
  fi

  local observed_at
  observed_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')

  local tmp_path="${out_dir}/claude-code.json.tmp"
  local final_path="${out_dir}/claude-code.json"
  jq -n --argjson limits "$limits" --arg observedAt "$observed_at" \
    '{"schemaVersion":1,"agentId":"claude-code","observedAt":$observedAt,"limits":$limits}' \
    > "$tmp_path" 2>/dev/null && mv -f "$tmp_path" "$final_path" 2>/dev/null
}
