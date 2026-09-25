// The Sql seam on Node's built-in SQLite, so the stores are tested without the Workers runtime.

import { DatabaseSync } from "node:sqlite";
import type { Sql, SqlValue } from "../src/sql.ts";

export function nodeSql(): Sql {
  const db = new DatabaseSync(":memory:");
  let depth = 0;
  return {
    all: <T>(query: string, ...params: SqlValue[]) => db.prepare(query).all(...params) as T[],
    run: (query: string, ...params: SqlValue[]) => {
      db.prepare(query).run(...params);
    },
    transaction: <T>(fn: () => T) => {
      if (depth > 0) return fn();
      depth++;
      db.exec("BEGIN");
      try {
        const result = fn();
        db.exec("COMMIT");
        return result;
      } catch (error) {
        db.exec("ROLLBACK");
        throw error;
      } finally {
        depth--;
      }
    },
  };
}
