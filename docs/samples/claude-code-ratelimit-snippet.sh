#!/usr/bin/env bash
# squirrel-notifier 連携: claude-code の statusLine JSON からレートリミット状態を
# 共通スキーマに変換してローカルファイルへ書き出す（#139）。
#
# 使い方: 既存の statusline スクリプト（例: ~/.claude/statusline-command.sh）で
# `input=$(cat)` により stdin の JSON を読み込んだ直後に、以下の関数呼び出しを追記する。
#   write_squirrel_notifier_ratelimit_status "$input"
#
# 依存: jq, GNU date（Git for Windows の bash に同梱）

write_squirrel_notifier_ratelimit_status() {
  local input="$1"
  local out_dir="${LOCALAPPDATA}/SquirrelNotifier/ratelimit-status"
  mkdir -p "$out_dir" 2>/dev/null || return 0

  local five_reset seven_reset limits="[]"
  five_reset=$(echo "$input" | jq -r '.rate_limits.five_hour.resets_at // empty' 2>/dev/null)
  seven_reset=$(echo "$input" | jq -r '.rate_limits.seven_day.resets_at // empty' 2>/dev/null)

  if [ -n "$five_reset" ]; then
    local five_iso
    five_iso=$(date -u -d "@${five_reset}" '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null)
    if [ -n "$five_iso" ]; then
      limits=$(echo "$limits" | jq --arg id "claude-code-5h" --arg label "claude-code 5時間枠" --arg reset "$five_iso" \
        '. + [{"id":$id,"label":$label,"resetAt":$reset}]')
    fi
  fi

  if [ -n "$seven_reset" ]; then
    local seven_iso
    seven_iso=$(date -u -d "@${seven_reset}" '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null)
    if [ -n "$seven_iso" ]; then
      limits=$(echo "$limits" | jq --arg id "claude-code-7d" --arg label "claude-code 週次枠" --arg reset "$seven_iso" \
        '. + [{"id":$id,"label":$label,"resetAt":$reset}]')
    fi
  fi

  local tmp_path="${out_dir}/claude-code.json.tmp"
  local final_path="${out_dir}/claude-code.json"
  echo "{\"limits\":${limits}}" > "$tmp_path" 2>/dev/null && mv -f "$tmp_path" "$final_path" 2>/dev/null
}
