"""
Full smoke test for the Recall API (Lab 1 + Lab 2 flows).
Covers: guest reads, auth (register/login/refresh rotation/reuse detection),
CRUD for all 3 objects as owner, 403 foreign-resource, admin override,
review feature (SM-2-lite), bulk import, and all negative codes.
Run: python labs/lab1/smoke_test.py
"""
import json
import sys
import time
import urllib.error
import urllib.request

BASE = "http://localhost:15080/api/v1"


def call(method, path, body=None, token=None):
    url = f"{BASE}{path}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            payload = resp.read().decode()
            return resp.status, json.loads(payload) if payload else None
    except urllib.error.HTTPError as e:
        payload = e.read().decode()
        return e.code, json.loads(payload) if payload else None


def wait_for_api(timeout=90):
    for _ in range(timeout):
        try:
            status, _ = call("GET", "/courses")
            if status == 200:
                return True
        except Exception:
            pass
        time.sleep(1)
    return False


def main():
    if not wait_for_api():
        print("FAIL: API did not come up in time")
        sys.exit(1)

    results = []

    def check(name, ok, detail=""):
        results.append((name, ok))
        print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}")

    # ---------- Guest flows ----------
    s, b = call("GET", "/courses?page=1&pageSize=5")
    check("guest lists public courses", s == 200 and b["meta"]["totalItems"] >= 5, f"s={s}")

    s, b = call("GET", "/courses?page=1&pageSize=2")
    check("pagination", s == 200 and len(b["data"]) == 2 and "next" in b["links"], f"s={s}")

    s, b = call("GET", "/courses?search=database")
    check("search filter", s == 200 and any("Database" in c["title"] for c in b["data"]), f"s={s}")

    s, _ = call("GET", "/courses/99999")
    check("404 missing course", s == 404, f"s={s}")

    s, _ = call("POST", "/courses", {"code": "X", "title": "X"})
    check("401 unauthenticated create", s == 401, f"s={s}")

    s, b = call("GET", "/courses/1/overview")
    check("composite overview (guest, public)", s == 200 and "stateBreakdown" in b, f"s={s}")

    s, b = call("GET", "/courses/1/topics/1/cards")
    check("hierarchical 3-object endpoint", s == 200 and len(b["data"]) >= 3, f"s={s}")

    s, _ = call("GET", "/me/dashboard")
    check("401 dashboard without auth", s == 401, f"s={s}")

    # ---------- Auth ----------
    # Unique per run: the API keeps users across test runs (no DB reset required).
    run_id = str(int(time.time()))
    smoke_email = f"smoke-{run_id}@recall.local"
    s, b = call("POST", "/auth/register", {"email": smoke_email, "password": "Smoke12345!", "displayName": "Smoke Tester"})
    check("register 201", s == 201, f"s={s}")
    smoke_token = b["accessToken"] if s == 201 else ""
    smoke_refresh = b["refreshToken"] if s == 201 else ""

    s, _ = call("POST", "/auth/register", {"email": smoke_email, "password": "Smoke12345!", "displayName": "Dup"})
    check("register duplicate 409", s == 409, f"s={s}")

    s, _ = call("POST", "/auth/register", {"email": "bad", "password": "x", "displayName": ""})
    check("register invalid 400", s == 400, f"s={s}")

    s, b = call("POST", "/auth/login", {"email": "author@recall.local", "password": "WRONG"})
    check("login wrong password 401", s == 401, f"s={s}")

    s, b = call("POST", "/auth/login", {"email": "author@recall.local", "password": "Author123!"})
    check("login author 200", s == 200, f"s={s}")
    author_token = b["accessToken"] if s == 200 else ""
    author_refresh = b["refreshToken"] if s == 200 else ""

    s, b = call("POST", "/auth/login", {"email": "learner@recall.local", "password": "Learner123!"})
    check("login learner 200", s == 200, f"s={s}")
    learner_token = b["accessToken"] if s == 200 else ""

    s, b = call("POST", "/auth/login", {"email": "admin@recall.local", "password": "Admin123!"})
    check("login admin 200", s == 200, f"s={s}")
    admin_token = b["accessToken"] if s == 200 else ""

    # Refresh rotation
    s, b = call("POST", "/auth/refresh", {"refreshToken": smoke_refresh})
    check("refresh rotation 200 + new pair", s == 200 and b["refreshToken"] != smoke_refresh, f"s={s}")
    rotated_away = smoke_refresh
    new_refresh = b["refreshToken"] if s == 200 else ""

    # Reuse of rotated token -> 401 + family revoked
    s, b = call("POST", "/auth/refresh", {"refreshToken": rotated_away})
    check("refresh reuse 401 (replay protection)", s == 401, f"s={s}")

    # After family revocation, the new one is also dead
    s, _ = call("POST", "/auth/refresh", {"refreshToken": new_refresh})
    check("family revoked after reuse", s == 401, f"s={s}")

    # Logout revokes
    s, b = call("POST", "/auth/login", {"email": "author@recall.local", "password": "Author123!"})
    author_refresh2 = b["refreshToken"]
    s, _ = call("POST", "/auth/logout", {"refreshToken": author_refresh2}, token=b["accessToken"])
    check("logout 204", s == 204, f"s={s}")
    s, _ = call("POST", "/auth/refresh", {"refreshToken": author_refresh2})
    check("refresh after logout 401", s == 401, f"s={s}")

    # ---------- Course CRUD as owner ----------
    s, b = call("POST", "/courses", {"code": "SMOKE-1", "title": "Smoke Course", "description": "temp", "visibility": "Private"}, token=author_token)
    check("create course 201", s == 201, f"s={s}")
    course_id = b["id"] if s == 201 else 0

    s, b = call("GET", f"/courses/{course_id}", token=author_token)
    check("read own private course 200", s == 200, f"s={s}")

    s, b = call("GET", f"/courses/{course_id}/overview", token=author_token)
    check("overview of empty course 200 (no crash)", s == 200 and b["cardCount"] == 0 and b["difficultyMix"]["hardest"] == 0, f"s={s}")

    # Pagination overflow no longer 500s
    s, b = call("GET", "/courses?page=2147483647&pageSize=100")
    check("huge page handled (no 500)", s == 200, f"s={s}")

    # Malformed refresh token -> 401, not 500
    s, b = call("POST", "/auth/refresh", {"refreshToken": "%%%garbage%%%"})
    check("malformed refresh token 401 (no 500)", s == 401, f"s={s}")

    # Empty PATCH title rejected
    s, b = call("PATCH", f"/courses/{course_id}", {"title": "   "}, token=author_token)
    check("400 empty PATCH title", s == 400, f"s={s}")

    # Import to a missing topic: authenticated, so the 404 wins over 401
    s, b = call("POST", "/topics/9999/cards/import", {"cards": [None]}, token=author_token)
    check("import to missing topic 404", s == 404, f"s={s}")

    s, b = call("GET", f"/courses/{course_id}")
    check("guest cannot read private course 404", s == 404, f"s={s}")

    s, b = call("PATCH", f"/courses/{course_id}", {"title": "Smoke Course v2"}, token=author_token)
    check("patch own course 200", s == 200 and b["title"] == "Smoke Course v2", f"s={s}")

    # Foreign contributor: 403 (learner does not own it)
    s, _ = call("PATCH", f"/courses/{course_id}", {"title": "Hijack"}, token=learner_token)
    check("403 foreign contributor patch", s == 403, f"s={s}")

    # Admin can edit any course
    s, _ = call("PATCH", f"/courses/{course_id}", {"description": "admin edited"}, token=admin_token)
    check("admin can edit any course 200", s == 200, f"s={s}")

    # ---------- Topic CRUD ----------
    s, b = call("POST", f"/courses/{course_id}/topics", {"title": "Smoke Topic", "summary": "temp"}, token=author_token)
    check("create topic 201", s == 201, f"s={s}")
    topic_id = b["id"] if s == 201 else 0

    s, b = call("GET", f"/topics/{topic_id}", token=author_token)
    check("read topic 200", s == 200, f"s={s}")

    s, b = call("PATCH", f"/topics/{topic_id}", {"summary": "updated"}, token=author_token)
    check("patch topic 200", s == 200, f"s={s}")

    s, b = call("GET", f"/courses/{course_id}/topics", token=author_token)
    check("list topics by course", s == 200 and len(b["data"]) >= 1, f"s={s}")

    s, _ = call("DELETE", f"/courses/{course_id}", token=author_token)
    check("409 delete course with topics", s == 409, f"s={s}")

    # ---------- Card CRUD ----------
    s, b = call("POST", f"/topics/{topic_id}/cards", {"question": "Smoke Q?", "answer": "Smoke A.", "difficulty": 3}, token=author_token)
    check("create card 201", s == 201, f"s={s}")
    card_id = b["id"] if s == 201 else 0

    s, b = call("GET", f"/cards/{card_id}", token=author_token)
    check("read card 200", s == 200, f"s={s}")

    s, b = call("PATCH", f"/cards/{card_id}", {"difficulty": 4, "tags": "smoke"}, token=author_token)
    check("patch card 200", s == 200 and b["difficulty"] == 4, f"s={s}")

    s, _ = call("POST", f"/topics/{topic_id}/cards", {"question": "Q", "answer": "A", "difficulty": 9}, token=author_token)
    check("400 invalid difficulty", s == 400, f"s={s}")

    s, _ = call("POST", f"/topics/{topic_id}/cards", {"question": "", "answer": "A"}, token=author_token)
    check("400 empty question", s == 400, f"s={s}")

    # Null import row rejected with row number (on a real topic)
    s, b = call("POST", f"/topics/{topic_id}/cards/import", {"cards": [None]}, token=author_token)
    check("400 null import row", s == 400, f"s={s}")

    # ---------- Review feature ----------
    s, b = call("GET", "/review/queue", token=author_token)
    check("review queue 200", s == 200 and b["count"] >= 1, f"s={s}, due={b.get('count') if s == 200 else 0}")

    if s == 200 and b["data"]:
        due_card = b["data"][0]["card"]
        due_id = due_card["id"]
        old_due = due_card.get("dueAt")
        s2, b2 = call("POST", f"/cards/{due_id}/review", {"rating": "good"}, token=author_token)
        check("review rating 200 + rescheduled", s2 == 200 and b2["nextDueAt"] not in (None, old_due), f"s={s2}")

    s, _ = call("POST", f"/cards/{card_id}/review", {"rating": "superb"}, token=author_token)
    check("400 invalid rating", s == 400, f"s={s}")

    # Foreign learner cannot review author's card
    s, _ = call("POST", f"/cards/{card_id}/review", {"rating": "good"}, token=learner_token)
    check("403 foreign review", s == 403, f"s={s}")

    # ---------- Bulk import ----------
    s, b = call("POST", f"/topics/{topic_id}/cards/import", {"cards": [
        {"question": "IQ1?", "answer": "IA1"},
        {"question": "IQ2?", "answer": "IA2"},
    ]}, token=author_token)
    check("import 201", s == 201 and b["imported"] == 2, f"s={s}")

    s, b = call("POST", f"/topics/{topic_id}/cards/import", {"cards": [{"question": "", "answer": "x"}]}, token=author_token)
    check("import invalid row 400", s == 400, f"s={s}")

    # ---------- Dashboard ----------
    s, b = call("GET", "/me/dashboard", token=author_token)
    check("dashboard 200 + dueToday", s == 200 and "dueToday" in b, f"s={s}")

    # ---------- Migration-focused value checks (re-review item B) ----------
    # Verify computed VALUES of the grouped/ordered/nullable queries the EF10
    # migration could have silently changed — not just response shape.
    # Fixture: a private course + topic with 3 known-state cards.
    s, b = call("POST", "/courses", {"code": "FIXQ-1", "title": "Query Fixture", "visibility": "Private"}, token=author_token)
    qcourse = b["id"] if s == 201 else 0
    s, b = call("POST", f"/courses/{qcourse}/topics", {"title": "Q Topic"}, token=author_token)
    qtopic = b["id"] if s == 201 else 0

    s, b = call("POST", f"/topics/{qtopic}/cards", {"question": "Q-new?", "answer": "A", "difficulty": 2}, token=author_token)
    card_new = b["id"] if s == 201 else 0
    s, b = call("POST", f"/topics/{qtopic}/cards", {"question": "Q-overdue?", "answer": "A", "difficulty": 3}, token=author_token)
    card_overdue = b["id"] if s == 201 else 0
    s, b = call("POST", f"/topics/{qtopic}/cards", {"question": "Q-future?", "answer": "A", "difficulty": 5}, token=author_token)
    card_future = b["id"] if s == 201 else 0
    # Set SRS states via controlled reviews: overdue card due now ("again"),
    # future card due in 3 days ("easy"); the new card stays never-reviewed.
    s, _ = call("POST", f"/cards/{card_overdue}/review", {"rating": "again"}, token=author_token)
    # future: review with "easy" -> interval 3d => dueAt in future (not due)
    s, _ = call("POST", f"/cards/{card_future}/review", {"rating": "easy"}, token=author_token)
    # card_new: never reviewed -> dueAt null -> included in queue as "new"

    # 1) Review queue ordering + membership: null-due first, then oldest due; future excluded
    s, b = call("GET", "/review/queue?limit=100", token=author_token)
    qids = [item["card"]["id"] for item in b.get("data", [])]
    check("queue includes null-due (new) card", card_new in qids, f"ids={qids[:5]}")
    check("queue includes overdue card", card_overdue in qids)
    check("queue excludes future card", card_future not in qids)
    if card_new in qids and card_overdue in qids:
        check("queue orders null-due before overdue", qids.index(card_new) < qids.index(card_overdue))
    # foreign exclusion: learner's queue must not contain author's cards
    s, b = call("GET", "/review/queue?limit=100", token=learner_token)
    lqids = [item["card"]["id"] for item in b.get("data", [])]
    check("queue excludes another owner's cards", card_new not in lqids and card_overdue not in lqids)

    # 2) Course overview aggregates: exact known counts for the fixture course
    s, b = call("GET", f"/courses/{qcourse}/overview", token=author_token)
    check("overview cardCount == 3", s == 200 and b["cardCount"] == 3, f"got {b.get('cardCount')}")
    check("overview topicCount == 1", s == 200 and b["topicCount"] == 1)
    check("overview difficulty mix: hardest == 5", s == 200 and b["difficultyMix"]["hardest"] == 5, f"got {b.get('difficultyMix', {}).get('hardest')}")
    check("overview per-topic count == 3", s == 200 and b["topics"][0]["cardCount"] == 3)

    # 3) Dashboard value check: the overdue fixture card must be counted.
    # The dashboard is cumulative over the author's whole dataset (seed cards
    # included), so assert membership via the queue instead of an absolute number:
    s, b = call("GET", "/me/dashboard", token=author_token)
    s2, b2 = call("GET", "/review/queue?limit=100", token=author_token)
    fixture_due_ids = [item["card"]["id"] for item in b2.get("data", []) if item["card"]["id"] in (card_new, card_overdue)]
    check("dashboard counts fixture cards as due (dueToday >= 1, seed-inclusive)",
          s == 200 and b["dueToday"] >= 1 and len(fixture_due_ids) >= 1,
          f"dueToday={b.get('dueToday')} fixtureInQueue={len(fixture_due_ids)}")

    # ---------- Session/patch regressions (Astra medium round) ----------
    # 1) Optional-field PATCH with "" must clear, not 400 (CheckOptionalText fix)
    s, b = call("POST", "/courses", {"code": "REG-1", "title": "Regression", "description": "x"}, token=author_token)
    rcourse = b["id"] if s == 201 else 0
    s, b = call("PATCH", f"/courses/{rcourse}", {"description": ""}, token=author_token)
    check("PATCH clears optional description with empty string", s == 200 and b["description"] == "", f"s={s}")
    s, b = call("POST", f"/courses/{rcourse}/topics", {"title": "RT"}, token=author_token)
    rtopic = b["id"] if s == 201 else 0
    s, b = call("PATCH", f"/topics/{rtopic}", {"summary": ""}, token=author_token)
    check("PATCH clears topic summary with empty string", s == 200 and b["summary"] == "", f"s={s}")
    s, b = call("POST", f"/topics/{rtopic}/cards", {"question": "RQ?", "answer": "RA", "hint": "h", "tags": "t"}, token=author_token)
    rcard = b["id"] if s == 201 else 0
    s, b = call("PATCH", f"/cards/{rcard}", {"hint": "", "tags": ""}, token=author_token)
    check("PATCH clears card hint/tags with empty string", s == 200 and b["hint"] is None and b["tags"] == "", f"s={s}")

    # 2) Invalid visibility on create now 400 (was silently coerced to Private)
    s, _ = call("POST", "/courses", {"code": "REG-2", "title": "BadVis", "visibility": "Bogus"}, token=author_token)
    check("create with invalid visibility 400", s == 400, f"s={s}")

    # 3) Nonpositive position on topic create now 400 (was silently accepted)
    s, _ = call("POST", f"/courses/{rcourse}/topics", {"title": "BadPos", "position": 0}, token=author_token)
    check("create topic with position 0 -> 400", s == 400, f"s={s}")

    # 4) Cross-site origin gate: spoofed disallowed Origin on refresh -> 403
    req = urllib.request.Request(f"{BASE}/auth/refresh", data=b"{}", method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("Origin", "https://evil.example.com")
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            rstatus = resp.status
    except urllib.error.HTTPError as e:
        rstatus = e.code
    check("auth origin gate: evil Origin on refresh -> 403", rstatus == 403, f"s={rstatus}")
    # ...but an allowed origin still works (login + refresh with allowed origin)
    req = urllib.request.Request(f"{BASE}/auth/login", data=json.dumps({"email": "author@recall.local", "password": "Author123!"}).encode(), method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("Origin", "http://localhost:15173")
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            ostatus, obody = resp.status, json.loads(resp.read().decode())
    except urllib.error.HTTPError as e:
        ostatus, obody = e.code, json.loads(e.read().decode())
    # The VPS path is same-origin. Its hostname is intentionally not part of
    # Cors:AllowedOrigins, so matching Origin + Host must pass independently.
    same_req = urllib.request.Request(f"{BASE}/auth/login", data=json.dumps({"email": "author@recall.local", "password": "Author123!"}).encode(), method="POST")
    same_req.add_header("Content-Type", "application/json")
    same_req.add_header("Origin", "http://localhost:15080")
    try:
        with urllib.request.urlopen(same_req, timeout=15) as resp:
            same_status = resp.status
    except urllib.error.HTTPError as e:
        same_status = e.code
    check("auth origin gate: configured fallback and matching same-origin hosts pass", ostatus == 200 and same_status == 200, f"configured={ostatus}, same={same_status}")

    # 5) Refresh rotation still works after the transactional rewrite
    s, b = call("POST", "/auth/login", {"email": "author@recall.local", "password": "Author123!"})
    rot_refresh = b["refreshToken"]
    s, b = call("POST", "/auth/refresh", {"refreshToken": rot_refresh})
    check("rotation post-fix: 200 + new pair", s == 200 and b["refreshToken"] != rot_refresh, f"s={s}")
    s, _ = call("POST", "/auth/refresh", {"refreshToken": rot_refresh})
    check("rotation post-fix: reuse of old -> 401", s == 401, f"s={s}")

    # cleanup regression fixture
    if rcard:
        call("DELETE", f"/cards/{rcard}", token=author_token)
    call("DELETE", f"/topics/{rtopic}", token=author_token)
    call("DELETE", f"/courses/{rcourse}", token=author_token)

    # ---------- Cleanup + delete chain ----------
    # Clean the query fixture first (cards -> topic -> course)
    for cid in (card_new, card_overdue, card_future):
        if cid:
            call("DELETE", f"/cards/{cid}", token=author_token)
    call("DELETE", f"/topics/{qtopic}", token=author_token)
    call("DELETE", f"/courses/{qcourse}", token=author_token)

    s, _ = call("DELETE", f"/cards/{card_id}", token=author_token)
    check("delete card 204", s == 204, f"s={s}")

    s, _ = call("GET", f"/cards/{card_id}", token=author_token)
    check("404 after delete", s == 404, f"s={s}")

    s, _ = call("DELETE", f"/topics/{topic_id}", token=author_token)
    check("409 delete topic with cards (imported ones)", s == 409, f"s={s}")

    # delete imported cards via list
    s, b = call("GET", f"/topics/{topic_id}/cards", token=author_token)
    if s == 200:
        for c in b["data"]:
            call("DELETE", f"/cards/{c['id']}", token=author_token)
    s, _ = call("DELETE", f"/topics/{topic_id}", token=author_token)
    check("delete topic 204 after emptying", s == 204, f"s={s}")

    s, _ = call("DELETE", f"/courses/{course_id}", token=author_token)
    check("delete course 204 after emptying", s == 204, f"s={s}")

    passed = sum(1 for _, ok in results if ok)
    print(f"\n{passed}/{len(results)} passed")
    sys.exit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
